using Content.Client._Ganimed.Fonts; // Ganimed-Add (UI font)
using JetBrains.Annotations;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Graphics;
using Robust.Shared.Utility;

namespace Content.Client.Resources
{
    [PublicAPI]
    public static class ResourceCacheExtensions
    {
        public static Texture GetTexture(this IResourceCache cache, ResPath path)
        {
            return cache.GetResource<TextureResource>(path);
        }

        public static Texture GetTexture(this IResourceCache cache, string path)
        {
            return GetTexture(cache, new ResPath(path));
        }

        public static Font GetFont(this IResourceCache cache, ResPath path, int size)
        {
            // Ganimed-Add-Start (UI font): the stock Noto Sans faces are swapped for the font the player picked
            if (IoCManager.Resolve<UserFontManager>().TrySubstitute(path, out var userFontStack))
                return cache.GetFont(userFontStack, size);
            // Ganimed-Add-End

            return new VectorFont(cache.GetResource<FontResource>(path), size);
        }

        public static Font GetFont(this IResourceCache cache, string path, int size)
        {
            return cache.GetFont(new ResPath(path), size);
        }

        public static Font GetFont(this IResourceCache cache, ResPath[] path, int size)
        {
            // The player's font goes first, the stock stack stays behind it as the glyph fallback
            path = IoCManager.Resolve<UserFontManager>().Substitute(path); // Ganimed-Add (UI font)
            var fs = new Font[path.Length];
            for (var i = 0; i < path.Length; i++)
                fs[i] = new VectorFont(cache.GetResource<FontResource>(path[i]), size);

            return new StackedFont(fs);
        }

        public static Font GetFont(this IResourceCache cache, string[] path, int size)
        {
            var rp = new ResPath[path.Length];
            for (var i = 0; i < path.Length; i++)
                rp[i] = new ResPath(path[i]);

            return cache.GetFont(rp, size);
        }
    }
}
