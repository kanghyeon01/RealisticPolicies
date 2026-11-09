using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// Draws the "unanswered lawsuit" notification icon (docs/previews/scales-icon.svg: the game's notification hexagon
    /// in warning yellow with a pair of scales) into a texture with the same size, mip count and format as the game's
    /// own notification icons. The game copies every icon into one texture array, so anything else would not fit.
    /// </summary>
    public static class LawsuitIcon
    {
        private static readonly Color32 kYellow = new Color32(255, 184, 14, 255);
        private static readonly Color32 kGrey = new Color32(66, 66, 66, 255);

        /// <summary>Creates the icon like <paramref name="reference"/>, or returns null (with the reason) for an unsupported format.</summary>
        public static Texture2D Create(Texture2D reference, out string problem) => Create(reference, Draw, "RealisticPolicies.LawsuitIcon", out problem);

        /// <summary>
        /// Creates an icon drawn by <paramref name="draw"/> (size → bottom-up RGBA pixels) with the reference's size, mip count
        /// and format, or returns null (with the reason) for an unsupported format.
        /// </summary>
        public static Texture2D Create(Texture2D reference, System.Func<int, Color32[]> draw, string name, out string problem)
        {
            problem = null;
            int size = reference.width;
            if (reference.height != size)
            {
                problem = $"reference icon is not square ({reference.width}x{reference.height})";
                return null;
            }
            GraphicsFormat format = reference.graphicsFormat;
            Encoding encoding = EncodingOf(format);
            if (encoding == Encoding.Unsupported)
            {
                problem = $"unsupported icon format {format}";
                return null;
            }
            int mips = reference.mipmapCount;
            var texture = new Texture2D(size, size, format, mips, mips > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None)
            {
                name = name,
            };
            Color32[] level = draw(size);
            int levelSize = size;
            var data = new System.Collections.Generic.List<byte>();
            for (int m = 0; m < mips; m++)
            {
                data.AddRange(Encode(level, levelSize, encoding));
                if (levelSize > 1)
                {
                    level = Downsample(level, levelSize);
                    levelSize /= 2;
                }
            }
            texture.LoadRawTextureData(data.ToArray());
            texture.Apply(false, true);
            return texture;
        }

        // ------------------------------------------------------------------ drawing (128-unit space of the SVG)

        private static Color32[] Draw(int size)
        {
            var pixels = new Color32[size * size];
            const int samples = 4;
            float unit = 128f / size;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float hex = 0f;
                    float grey = 0f;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        for (int sx = 0; sx < samples; sx++)
                        {
                            // Texture rows go bottom-up; the SVG's go top-down.
                            float px = (x + (sx + 0.5f) / samples) * unit;
                            float py = 128f - (y + (sy + 0.5f) / samples) * unit;
                            if (InHexagon(px, py))
                            {
                                hex += 1f;
                                if (InScales(px, py))
                                {
                                    grey += 1f;
                                }
                            }
                        }
                    }
                    float n = samples * samples;
                    float coverage = hex / n;
                    float greyShare = hex > 0f ? grey / hex : 0f;
                    Color32 c = Color32.Lerp(kYellow, kGrey, greyShare);
                    c.a = (byte)math.round(coverage * 255f);
                    pixels[y * size + x] = c;
                }
            }
            return pixels;
        }

        /// <summary>The game's hexagon path with its transform matrix(0.976563,0,0,0.942198,1.5,1.5).</summary>
        private static bool InHexagon(float x, float y)
        {
            float u = (x - 1.5f) / 0.976563f;
            float v = (y - 1.5f) / 0.942198f;
            if (u < 8.574f || u > 119.426f || v < 0f || v > 132.668f)
            {
                return false;
            }
            float dx = math.abs(u - 64f);
            // Top and bottom slopes from (64,0) to (119.426,33.167).
            float slope = 33.167f / 55.426f;
            return v >= dx * slope && v <= 132.668f - dx * slope;
        }

        private static bool InScales(float x, float y)
        {
            return InRect(x, y, 61f, 32f, 6f, 62f)
                || InRoundRect(x, y, 26f, 36f, 76f, 6f, 3f)
                || InCircle(x, y, 64f, 31f, 6f)
                || OnSegment(x, y, 32f, 41f, 22f, 70f, 1.5f)
                || OnSegment(x, y, 32f, 41f, 42f, 70f, 1.5f)
                || OnSegment(x, y, 96f, 41f, 86f, 70f, 1.5f)
                || OnSegment(x, y, 96f, 41f, 106f, 70f, 1.5f)
                || (y >= 70f && InCircle(x, y, 32f, 70f, 14f))
                || (y >= 70f && InCircle(x, y, 96f, 70f, 14f))
                || InRoundRect(x, y, 44f, 92f, 40f, 8f, 4f);
        }

        private static bool InRect(float x, float y, float left, float top, float width, float height) =>
            x >= left && x <= left + width && y >= top && y <= top + height;

        private static bool InRoundRect(float x, float y, float left, float top, float width, float height, float radius)
        {
            if (!InRect(x, y, left, top, width, height))
            {
                return false;
            }
            float cx = math.clamp(x, left + radius, left + width - radius);
            float cy = math.clamp(y, top + radius, top + height - radius);
            return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius;
        }

        private static bool InCircle(float x, float y, float cx, float cy, float r) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;

        private static bool OnSegment(float x, float y, float x1, float y1, float x2, float y2, float halfWidth)
        {
            float2 p = new float2(x, y), a = new float2(x1, y1), b = new float2(x2, y2);
            float2 ab = b - a;
            float t = math.saturate(math.dot(p - a, ab) / math.dot(ab, ab));
            return math.distancesq(p, a + t * ab) <= halfWidth * halfWidth;
        }

        private static Color32[] Downsample(Color32[] src, int size)
        {
            int half = math.max(1, size / 2);
            var dst = new Color32[half * half];
            for (int y = 0; y < half; y++)
            {
                for (int x = 0; x < half; x++)
                {
                    int4 sum = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        for (int dx = 0; dx < 2; dx++)
                        {
                            Color32 c = src[math.min(size - 1, y * 2 + dy) * size + math.min(size - 1, x * 2 + dx)];
                            sum += new int4(c.r, c.g, c.b, c.a);
                        }
                    }
                    sum = (sum + 2) / 4;
                    dst[y * half + x] = new Color32((byte)sum.x, (byte)sum.y, (byte)sum.z, (byte)sum.w);
                }
            }
            return dst;
        }

        // ------------------------------------------------------------------ encoding

        private enum Encoding
        {
            Unsupported,
            Rgba32,
            Bc3,
            Bc7,
        }

        private static Encoding EncodingOf(GraphicsFormat format)
        {
            switch (format)
            {
                case GraphicsFormat.R8G8B8A8_SRGB:
                case GraphicsFormat.R8G8B8A8_UNorm:
                    return Encoding.Rgba32;
                case GraphicsFormat.RGBA_DXT5_SRGB:
                case GraphicsFormat.RGBA_DXT5_UNorm:
                    return Encoding.Bc3;
                case GraphicsFormat.RGBA_BC7_SRGB:
                case GraphicsFormat.RGBA_BC7_UNorm:
                    return Encoding.Bc7;
                default:
                    return Encoding.Unsupported;
            }
        }

        private static byte[] Encode(Color32[] pixels, int size, Encoding encoding)
        {
            if (encoding == Encoding.Rgba32)
            {
                var raw = new byte[size * size * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    raw[i * 4] = pixels[i].r;
                    raw[i * 4 + 1] = pixels[i].g;
                    raw[i * 4 + 2] = pixels[i].b;
                    raw[i * 4 + 3] = pixels[i].a;
                }
                return raw;
            }
            int blocks = math.max(1, (size + 3) / 4);
            var output = new byte[blocks * blocks * 16];
            var block = new Color32[16];
            for (int by = 0; by < blocks; by++)
            {
                for (int bx = 0; bx < blocks; bx++)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        int x = math.min(size - 1, bx * 4 + (i & 3));
                        int y = math.min(size - 1, by * 4 + (i >> 2));
                        block[i] = pixels[y * size + x];
                    }
                    int offset = (by * blocks + bx) * 16;
                    if (encoding == Encoding.Bc7)
                    {
                        EncodeBc7Mode6(block, output, offset);
                    }
                    else
                    {
                        EncodeBc3(block, output, offset);
                    }
                }
            }
            return output;
        }

        /// <summary>BC7 mode 6: one subset, RGBA endpoints (7 bits + p-bit), 4-bit indices along the colour line.</summary>
        private static void EncodeBc7Mode6(Color32[] block, byte[] output, int offset)
        {
            int4 lo = 255, hi = 0;
            for (int i = 0; i < 16; i++)
            {
                int4 c = new int4(block[i].r, block[i].g, block[i].b, block[i].a);
                lo = math.min(lo, c);
                hi = math.max(hi, c);
            }
            // 7-bit endpoints with p-bit 0 reconstruct as even 8-bit values.
            int4 e0 = lo >> 1, e1 = (hi + 1) >> 1;
            e1 = math.min(e1, 127);
            int4 v0 = e0 << 1, v1 = e1 << 1;
            int[] weights = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };
            var indices = new int[16];
            float4 d = (float4)(v1 - v0);
            float dd = math.dot(d, d);
            for (int i = 0; i < 16; i++)
            {
                float4 p = new float4(block[i].r, block[i].g, block[i].b, block[i].a) - v0;
                float t = dd > 0f ? math.saturate(math.dot(p, d) / dd) : 0f;
                int best = 0;
                float bestError = float.MaxValue;
                for (int k = 0; k < 16; k++)
                {
                    float error = math.abs(t * 64f - weights[k]);
                    if (error < bestError)
                    {
                        bestError = error;
                        best = k;
                    }
                }
                indices[i] = best;
            }
            // The first index's top bit is implicit 0: swap the endpoints if needed.
            if (indices[0] >= 8)
            {
                int4 swap = e0;
                e0 = e1;
                e1 = swap;
                for (int i = 0; i < 16; i++)
                {
                    indices[i] = 15 - indices[i];
                }
            }
            var bits = new BitWriter(output, offset);
            bits.Write(1 << 6, 7);
            bits.Write(e0.x, 7); bits.Write(e1.x, 7);
            bits.Write(e0.y, 7); bits.Write(e1.y, 7);
            bits.Write(e0.z, 7); bits.Write(e1.z, 7);
            bits.Write(e0.w, 7); bits.Write(e1.w, 7);
            bits.Write(0, 1);
            bits.Write(0, 1);
            bits.Write(indices[0], 3);
            for (int i = 1; i < 16; i++)
            {
                bits.Write(indices[i], 4);
            }
        }

        /// <summary>BC3 (DXT5): interpolated alpha block, then a DXT1 colour block in four-colour mode.</summary>
        private static void EncodeBc3(Color32[] block, byte[] output, int offset)
        {
            int aLo = 255, aHi = 0;
            for (int i = 0; i < 16; i++)
            {
                aLo = math.min(aLo, block[i].a);
                aHi = math.max(aHi, block[i].a);
            }
            output[offset] = (byte)aHi;
            output[offset + 1] = (byte)aLo;
            ulong alphaBits = 0;
            for (int i = 0; i < 16; i++)
            {
                int index;
                if (aHi == aLo)
                {
                    index = 0;
                }
                else
                {
                    // Palette: 0 = hi, 1 = lo, 2..7 = blends from hi to lo.
                    float t = (aHi - block[i].a) / (float)(aHi - aLo);
                    int step = (int)math.round(t * 7f);
                    index = step == 0 ? 0 : step == 7 ? 1 : step + 1;
                }
                alphaBits |= (ulong)index << (3 * i);
            }
            for (int b = 0; b < 6; b++)
            {
                output[offset + 2 + b] = (byte)(alphaBits >> (8 * b));
            }

            int3 lo = 255, hi = 0;
            for (int i = 0; i < 16; i++)
            {
                if (block[i].a == 0)
                {
                    continue;
                }
                int3 c = new int3(block[i].r, block[i].g, block[i].b);
                lo = math.min(lo, c);
                hi = math.max(hi, c);
            }
            if (lo.x > hi.x)
            {
                lo = hi = 0;
            }
            ushort c0 = To565(hi), c1 = To565(lo);
            if (c0 < c1)
            {
                ushort s = c0;
                c0 = c1;
                c1 = s;
                int3 t = hi;
                hi = lo;
                lo = t;
            }
            output[offset + 8] = (byte)c0;
            output[offset + 9] = (byte)(c0 >> 8);
            output[offset + 10] = (byte)c1;
            output[offset + 11] = (byte)(c1 >> 8);
            uint colorBits = 0;
            float3 d = hi - lo;
            float dd = math.dot(d, d);
            for (int i = 0; i < 16; i++)
            {
                float t = dd > 0f ? math.saturate(math.dot(new float3(block[i].r, block[i].g, block[i].b) - lo, d) / dd) : 1f;
                // Palette: 0 = c0 (hi), 1 = c1 (lo), 2 = 2/3 hi, 3 = 1/3 hi.
                int step = (int)math.round(t * 3f);
                int index = step == 3 ? 0 : step == 0 ? 1 : step == 2 ? 2 : 3;
                colorBits |= (uint)index << (2 * i);
            }
            for (int b = 0; b < 4; b++)
            {
                output[offset + 12 + b] = (byte)(colorBits >> (8 * b));
            }
        }

        private static ushort To565(int3 c) => (ushort)(((c.x >> 3) << 11) | ((c.y >> 2) << 5) | (c.z >> 3));

        private struct BitWriter
        {
            private readonly byte[] m_Output;
            private readonly int m_Offset;
            private int m_Bit;

            public BitWriter(byte[] output, int offset)
            {
                m_Output = output;
                m_Offset = offset;
                m_Bit = 0;
                for (int i = 0; i < 16; i++)
                {
                    output[offset + i] = 0;
                }
            }

            /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>, least significant first.</summary>
            public void Write(int value, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    if (((value >> i) & 1) != 0)
                    {
                        m_Output[m_Offset + (m_Bit >> 3)] |= (byte)(1 << (m_Bit & 7));
                    }
                    m_Bit++;
                }
            }
        }
    }
}
