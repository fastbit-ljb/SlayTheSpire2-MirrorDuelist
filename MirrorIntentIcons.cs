using System;
using System.IO;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace MirrorDuelistMod;

internal static class MirrorIntentIcons
{
    private static Texture2D? _steal;
    private static Texture2D? _playCard;

    public static Texture2D? Steal => _steal ??= Load("steal_intent.png");

    public static Texture2D? PlayCard => _playCard ??= Load("play_card_intent.png");

    private static Texture2D? Load(string name)
    {
        try
        {
            string resourceName = "MirrorDuelistMod.assets." + name;
            using Stream? stream = typeof(MirrorIntentIcons).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Log.Error("[MirrorDuelist] missing embedded intent icon: " + resourceName);
                return null;
            }
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var image = new Godot.Image();
            Error error = image.LoadPngFromBuffer(memory.ToArray());
            if (error != Error.Ok)
            {
                Log.Error($"[MirrorDuelist] failed to decode {name}: {error}");
                return null;
            }
            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception e)
        {
            Log.Error($"[MirrorDuelist] failed to load {name}: {e}");
            return null;
        }
    }
}
