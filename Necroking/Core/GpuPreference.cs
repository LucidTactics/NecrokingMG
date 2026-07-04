using System;
using System.Runtime.InteropServices;

namespace Necroking.Core;

/// <summary>
/// Hybrid-GPU (dual-GPU laptop) handling. MonoGame DesktopGL is an OpenGL app, and on
/// Optimus-style laptops Windows routes unknown OpenGL apps to the power-saving
/// integrated GPU by default — measured 2026-07: ~40 fps on an Intel UHD while the
/// discrete RTX 4060 sat idle. Native games opt into the discrete GPU by exporting
/// NvOptimusEnablement/AmdPowerXpressRequestHighPerformance, but a .NET apphost exe
/// cannot export PE data symbols, so instead we write the vendor-neutral per-app
/// preference Windows keeps in HKCU — the same value the Settings &gt; Display &gt;
/// Graphics UI writes. No admin rights needed. Written before the GL context exists;
/// if the driver only picks it up on the next launch, the in-game toast
/// (<see cref="Game1"/> LoadContent) tells the player to restart once.
/// </summary>
public static class GpuPreference
{
    const string RegPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>True when this launch created the preference entry (first run on this
    /// machine). The iGPU warning toast uses it to say "restart to apply" instead of
    /// pointing the player at Windows settings.</summary>
    public static bool WrotePreferenceThisLaunch;

    /// <summary>Write "high performance GPU" preference for this exe if the player has
    /// no explicit preference yet. Call before the graphics device / GL context is
    /// created (Program.Main). Respects an existing entry — the player may have
    /// deliberately chosen power saving.</summary>
    public static void EnsureHighPerformance()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        // `dotnet run` executes under the shared dotnet.exe host — tagging that would
        // force every .NET tool on the machine onto the discrete GPU.
        if (System.IO.Path.GetFileNameWithoutExtension(exe)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegPath);
            if (key == null) return;
            if (key.GetValue(exe) is string existing &&
                existing.Contains("GpuPreference=", StringComparison.OrdinalIgnoreCase))
                return;
            key.SetValue(exe, "GpuPreference=2;"); // 2 = high performance
            WrotePreferenceThisLaunch = true;
            DebugLog.Log("startup", $"GpuPreference: wrote high-performance entry for {exe}");
        }
        catch (Exception ex)
        {
            // Registry unavailable (locked-down machine) — the detect+warn toast still fires.
            DebugLog.Log("startup", $"GpuPreference: registry write failed: {ex.Message}");
        }
    }

    [DllImport("opengl32.dll", EntryPoint = "glGetString")]
    private static extern IntPtr GlGetString(uint name);

    /// <summary>GL_RENDERER of the ACTIVE context — the GPU actually rendering the game.
    /// Only valid after the GraphicsDevice exists and on the main thread (LoadContent is
    /// both). MonoGame DesktopGL doesn't surface this itself. Returns "" off-Windows or
    /// on failure.</summary>
    public static string ActiveRenderer()
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            IntPtr vend = GlGetString(0x1F00); // GL_VENDOR
            IntPtr rend = GlGetString(0x1F01); // GL_RENDERER
            string v = vend != IntPtr.Zero ? (Marshal.PtrToStringAnsi(vend) ?? "") : "";
            string r = rend != IntPtr.Zero ? (Marshal.PtrToStringAnsi(rend) ?? "") : "";
            return (v + " " + r).Trim();
        }
        catch { return ""; }
    }

    /// <summary>Does the renderer string look like an integrated GPU?</summary>
    public static bool IsIntegrated(string renderer) =>
        renderer.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
        renderer.Contains("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
        renderer.Contains("Vega", StringComparison.OrdinalIgnoreCase) ||
        renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Is a discrete GPU installed? Enumerates the display-adapter driver class
    /// in HKLM (readable without admin) — EnumDisplayDevices can miss an Optimus dGPU
    /// because it has no desktop attached.</summary>
    public static bool HasDiscreteAdapter()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return false;
            foreach (string sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !int.TryParse(sub, out _)) continue; // 0000, 0001, ...
                using var dev = cls.OpenSubKey(sub);
                if (dev?.GetValue("DriverDesc") is not string desc) continue;
                if (desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Quadro", StringComparison.OrdinalIgnoreCase) ||
                    ((desc.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                      desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) &&
                     desc.Contains("RX", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch { /* fall through */ }
        return false;
    }
}
