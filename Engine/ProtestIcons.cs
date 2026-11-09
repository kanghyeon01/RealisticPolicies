using Unity.Mathematics;
using UnityEngine;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// The protest notification icons (docs/previews/protest-panel.html): a white megaphone on an orange disc for a protest,
    /// a white flame on a red disc for a riot. Drawn into the game's notification icon format by <see cref="LawsuitIcon"/>.
    /// Coordinates are those of the preview's 64-unit SVG.
    /// </summary>
    public static class ProtestIcons
    {
        private static readonly Color32 kOrange = new Color32(255, 164, 45, 255);
        private static readonly Color32 kOrangeRim = new Color32(90, 50, 0, 255);
        private static readonly Color32 kRed = new Color32(233, 95, 74, 255);
        private static readonly Color32 kRedRim = new Color32(74, 15, 6, 255);
        private static readonly Color32 kWhite = new Color32(255, 255, 255, 255);

        public static Color32[] DrawProtest(int size) => Draw(size, kOrange, kOrangeRim, Megaphone);

        public static Color32[] DrawRiot(int size) => Draw(size, kRed, kRedRim, Flame);

        private static Color32[] Draw(int size, Color32 fill, Color32 rim, System.Func<float, float, int> symbol)
        {
            var pixels = new Color32[size * size];
            const int samples = 4;
            float unit = 64f / size;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float4 sum = 0f;
                    float covered = 0f;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        for (int sx = 0; sx < samples; sx++)
                        {
                            // Texture rows go bottom-up; the SVG's go top-down.
                            float px = (x + (sx + 0.5f) / samples) * unit;
                            float py = 64f - (y + (sy + 0.5f) / samples) * unit;
                            float d = math.distance(new float2(px, py), new float2(32f, 32f));
                            if (d > 30.5f)
                            {
                                continue;
                            }
                            covered += 1f;
                            Color32 c = d > 27.5f ? rim : fill;
                            int mark = symbol(px, py);
                            if (mark == 1)
                            {
                                c = kWhite;
                            }
                            else if (mark == 2)
                            {
                                c = fill;
                            }
                            sum += new float4(c.r, c.g, c.b, 255f);
                        }
                    }
                    float n = samples * samples;
                    if (covered <= 0f)
                    {
                        pixels[y * size + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }
                    float4 avg = sum / covered;
                    pixels[y * size + x] = new Color32((byte)avg.x, (byte)avg.y, (byte)avg.z, (byte)math.round(covered / n * 255f));
                }
            }
            return pixels;
        }

        /// <summary>Megaphone: horn M16 27 h7 l17 -9 v28 l-17 -9 h-7 z, handle, and a sound arc. 1 = white.</summary>
        private static int Megaphone(float x, float y)
        {
            // Mouthpiece box 16..23 × 27..37.
            if (x >= 16f && x <= 23f && y >= 27f && y <= 37f)
            {
                return 1;
            }
            // Horn: from x 23 (27..37) widening to x 40 (18..46).
            if (x >= 23f && x <= 40f)
            {
                float t = (x - 23f) / 17f;
                float top = math.lerp(27f, 18f, t);
                float bottom = math.lerp(37f, 46f, t);
                if (y >= top && y <= bottom)
                {
                    return 1;
                }
            }
            // Handle: parallelogram from (21,37)-(24,47) to (26,37)-(29,47).
            if (y >= 37f && y <= 47f)
            {
                float shift = (y - 37f) * 0.3f;
                if (x >= 21f + shift && x <= 26f + shift)
                {
                    return 1;
                }
            }
            // Sound arc right of the horn, radius 9 round (40, 32), 3 units thick, from -60° to 60°.
            float2 v = new float2(x - 39f, y - 32f);
            float r = math.length(v);
            if (r >= 7.5f && r <= 10.5f && v.x > 0f && math.abs(v.y) <= v.x * 1.4f)
            {
                return 1;
            }
            return 0;
        }

        /// <summary>Flame: a white teardrop with a red inner tongue. 1 = white, 2 = disc colour.</summary>
        private static int Flame(float x, float y)
        {
            // Outer flame: a circle at (32, 38) radius 12 below, a point rising to (32, 12) above.
            bool outer = InDrop(x, y, 32f, 38f, 12f, 12f);
            if (!outer)
            {
                return 0;
            }
            bool inner = InDrop(x, y, 32f, 44f, 6f, 30f);
            return inner ? 2 : 1;
        }

        /// <summary>A drop: a circle of radius <paramref name="r"/> at (cx, cy) whose top narrows to a point at <paramref name="tipY"/>.</summary>
        private static bool InDrop(float x, float y, float cx, float cy, float r, float tipY)
        {
            if (y >= cy)
            {
                return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;
            }
            if (y < tipY)
            {
                return false;
            }
            float t = (y - tipY) / (cy - tipY);
            float half = r * math.sqrt(t);
            return math.abs(x - cx) <= half;
        }
    }
}
