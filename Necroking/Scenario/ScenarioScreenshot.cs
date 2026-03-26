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

    public static bool TakeScreenshot(GraphicsDevice device, string name)
    {
        if (Disabled) return true;
        Directory.CreateDirectory("log/screenshots");

        string path = $"log/screenshots/{name}.png";
        try
        {
            int w = device.PresentationParameters.BackBufferWidth;
            int h = device.PresentationParameters.BackBufferHeight;
            var data = new Color[w * h];
            device.GetBackBufferData(data);

            using var rt = new RenderTarget2D(device, w, h);
            rt.SetData(data);

            using var stream = File.Create(path);
            rt.SaveAsPng(stream, w, h);

            Console.Error.WriteLine($"Screenshot saved: {path}");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save screenshot: {path} - {ex.Message}");
            return false;
        }
    }
}
