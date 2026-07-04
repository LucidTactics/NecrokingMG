using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Net;

namespace Necroking.Editor;

/// <summary>
/// Pause-menu → Multiplayer submenu. Host a session (bind IP + port + on/off
/// toggle) or join one (host IP + port + connect). Pure UI — all real work
/// happens in <see cref="NetSession"/> (Necroking/Net/, see its README before
/// touching that folder). Follows the SettingsWindow pattern: driven by the
/// shared EditorBase, closed via <see cref="WantsClose"/>.
/// </summary>
public class MultiplayerWindow
{
    private readonly EditorBase _ui;
    private NetSession _session = null!;

    /// <summary>Set to true when the user clicks Back (ESC goes through the modal layer).</summary>
    public bool WantsClose { get; set; }

    // Field state (committed on every frame like all EditorBase text fields).
    private string _hostBindIp = "0.0.0.0";
    private string _hostPort = NetProtocol.DefaultPort.ToString();
    private string _joinIp = "";
    private string _joinPort = NetProtocol.DefaultPort.ToString();

    private List<string>? _lanIps; // cached on first open

    private const int PanelW = 620;
    private const int PanelH = 600;

    private static readonly Color BannerActive = new(45, 110, 65, 240);  // green: hosting / connected
    private static readonly Color BannerTrying = new(150, 115, 40, 240); // amber: connecting
    private static readonly Color BannerIdle = new(45, 45, 65, 240);     // gray: offline

    public MultiplayerWindow(EditorBase ui)
    {
        _ui = ui;
    }

    public void SetSession(NetSession session) => _session = session;

    public void Draw(int screenW, int screenH)
    {
        _lanIps ??= NetSession.GetLocalIPv4s();

        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 180));

        int panelX = (screenW - PanelW) / 2;
        int panelY = (screenH - PanelH) / 2;
        _ui.DrawRect(new Rectangle(panelX, panelY, PanelW, PanelH), EditorBase.PanelBg);
        _ui.DrawBorder(new Rectangle(panelX, panelY, PanelW, PanelH), EditorBase.PanelBorder);
        _ui.DrawRect(new Rectangle(panelX, panelY, PanelW, 3), EditorBase.AccentColor);

        string title = "MULTIPLAYER";
        var titleSize = _ui.MeasureText(title);
        _ui.DrawText(title, new Vector2(panelX + PanelW / 2f - (int)(titleSize.X / 2f), panelY + 8), EditorBase.TextBright);

        int x = panelX + 20;
        int w = PanelW - 40;

        bool hosting = _session.Mode == NetMode.Host;
        bool clienting = _session.Mode == NetMode.Client;
        bool connected = _session.StatusLine.StartsWith("CONNECTED") || _session.StatusLine.StartsWith("HOSTING");

        // ── Live-state banner (prominent, top) ──────────────────────────
        // Flips the INSTANT Host/Connect is clicked — placed high and drawn as a
        // filled colored bar so it's unmistakable the buttons are wired even
        // before any peer answers. StartHost/Connect run inside DrawButton below,
        // but this bar is redrawn every frame from the live session state.
        Color bannerBg = _session.Mode switch
        {
            NetMode.Host => BannerActive,
            NetMode.Client when connected => BannerActive,
            NetMode.Client => BannerTrying,
            _ => BannerIdle,
        };
        var bannerRect = new Rectangle(panelX + 12, panelY + 30, PanelW - 24, 28);
        _ui.DrawRect(bannerRect, bannerBg);
        _ui.DrawBorder(bannerRect, EditorBase.PanelBorder);
        string bannerText = _session.StatusLine;
        var bSize = _ui.MeasureText(bannerText);
        _ui.DrawText(bannerText,
            new Vector2(panelX + PanelW / 2f - (int)(bSize.X / 2f),
                        panelY + 30 + (28 - (int)bSize.Y) / 2),
            EditorBase.TextBright);

        int y = panelY + 68;

        // ── Host section ────────────────────────────────────────────────
        _ui.DrawText("HOST A GAME", new Vector2(x, y), EditorBase.AccentColor);
        y += 22;
        _hostBindIp = _ui.DrawTextField("mp_host_ip", "Bind IP", _hostBindIp, x, y, w - 200);
        y += 28;
        _hostPort = _ui.DrawTextField("mp_host_port", "UDP Port", _hostPort, x, y, w - 200);
        y += 32;

        string hostBtn = hosting ? "Stop Hosting" : "Start Hosting";
        Color hostBtnBg = hosting ? new Color(120, 50, 50, 240) : EditorBase.ButtonBg;
        if (_ui.DrawButton(hostBtn, x, y, 180, 30, hostBtnBg))
        {
            Necroking.Core.DebugLog.Log("net", $"[UI] Host button clicked (hosting was {hosting})");
            if (hosting) _session.Stop();
            else _session.StartHost(_hostBindIp, ParsePort(_hostPort));
        }
        y += 38;

        _ui.DrawText("Leave Bind IP 0.0.0.0 (all adapters) unless you know why.", new Vector2(x, y), EditorBase.TextDim);
        y += 18;
        string lan = _lanIps.Count > 0 ? string.Join("   ", _lanIps) : "(none found)";
        _ui.DrawText($"Your LAN IPs: {lan}", new Vector2(x, y), EditorBase.TextDim);
        y += 18;
        _ui.DrawText("Internet friends need your PUBLIC IP + the UDP port forwarded.", new Vector2(x, y), EditorBase.TextDim);
        y += 28;

        // ── Join section ────────────────────────────────────────────────
        _ui.DrawText("JOIN A GAME", new Vector2(x, y), EditorBase.AccentColor);
        y += 22;
        _joinIp = _ui.DrawTextField("mp_join_ip", "Host IP", _joinIp, x, y, w - 200);
        y += 28;
        _joinPort = _ui.DrawTextField("mp_join_port", "UDP Port", _joinPort, x, y, w - 200);
        y += 32;

        string joinBtn = clienting ? "Disconnect" : "Connect";
        Color joinBtnBg = clienting ? new Color(120, 50, 50, 240) : EditorBase.ButtonBg;
        if (_ui.DrawButton(joinBtn, x, y, 180, 30, joinBtnBg))
        {
            Necroking.Core.DebugLog.Log("net", $"[UI] Connect button clicked (clienting was {clienting})");
            if (clienting) _session.Stop();
            else _session.Connect(_joinIp, ParsePort(_joinPort));
        }
        y += 38;

        // ── Log ─────────────────────────────────────────────────────────
        // Live-state now lives in the top banner; this is the event history.
        _ui.DrawText("LOG", new Vector2(x, y), EditorBase.AccentColor);
        y += 20;
        var log = _session.Log;
        if (log.Count == 0)
            _ui.DrawText("(no activity yet - click Start Hosting or Connect)",
                new Vector2(x, y), EditorBase.TextDim);
        int logLines = 7;
        for (int i = System.Math.Max(0, log.Count - logLines); i < log.Count; i++)
        {
            Color lineColor = IsErrorLine(log[i]) ? EditorBase.DangerColor : EditorBase.TextDim;
            _ui.DrawText(log[i], new Vector2(x, y), lineColor);
            y += 16;
        }

        // ── Back ────────────────────────────────────────────────────────
        if (_ui.DrawButton("Back", panelX + (PanelW - 120) / 2, panelY + PanelH - 42, 120, 30))
            WantsClose = true;
    }

    private static int ParsePort(string text)
        => int.TryParse(text.Trim(), out int p) && p is > 0 and < 65536 ? p : NetProtocol.DefaultPort;

    /// <summary>Heuristic: does this NetSession log line describe a failure?
    /// The log is plain strings (see NetSession.AddLog), so we match on the
    /// wording those AddLog calls use for problems.</summary>
    private static bool IsErrorLine(string line)
    {
        foreach (var kw in new[] { "failed", "invalid", "error", "rejected",
                                   "lost", "no host", "bad packet", "in use", "could not" })
            if (line.Contains(kw, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
