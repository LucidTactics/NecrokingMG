using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Scenario;

public static class ScenarioScreenshot
{
    public static bool Disabled { get; set; }

    public static void Init()
    {
        Directory.CreateDirectory("log/screenshots");
    }

    /// <summary>Capture the current backbuffer to log/screenshots/&lt;name&gt;.png.
    /// When <paramref name="targetW"/>/<paramref name="targetH"/> are &gt; 0 and differ
    /// from the backbuffer size, the image is downsampled to that size (linear filter)
    /// before saving — so a high-res render returns a small, readable PNG.</summary>
    public static bool TakeScreenshot(GraphicsDevice device, string name, int targetW = 0, int targetH = 0)
    {
        if (Disabled) return true;
        Directory.CreateDirectory("log/screenshots");

        string path = $"log/screenshots/{name}.png";
        try
        {
            int w = device.PresentationParameters.BackBufferWidth;
            int h = device.PresentationParameters.BackBufferHeight;
            // Capture the rendered frame FIRST so any render-target juggling below
            // can't corrupt what we save.
            var data = new Color[w * h];
            device.GetBackBufferData(data);

            using var rt = new RenderTarget2D(device, w, h);
            rt.SetData(data);

            bool downsample = targetW > 0 && targetH > 0 && (targetW != w || targetH != h);
            if (downsample)
            {
                using var scaled = new RenderTarget2D(device, targetW, targetH);
                device.SetRenderTarget(scaled);
                device.Clear(Color.Black);
                using (var sb = new SpriteBatch(device))
                {
                    sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
                    sb.Draw(rt, new Rectangle(0, 0, targetW, targetH), Color.White);
                    sb.End();
                }
                device.SetRenderTarget(null); // restore backbuffer for Present
                using var stream = File.Create(path);
                scaled.SaveAsPng(stream, targetW, targetH);
                Console.Error.WriteLine($"Screenshot saved: {path} ({targetW}x{targetH} from {w}x{h})");
            }
            else
            {
                using var stream = File.Create(path);
                rt.SaveAsPng(stream, w, h);
                Console.Error.WriteLine($"Screenshot saved: {path}");
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save screenshot: {path} - {ex.Message}");
            return false;
        }
    }
}
