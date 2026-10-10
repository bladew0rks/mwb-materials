using System;

namespace mwb_materials.MwbMats
{
    //widens surfaceggx roughness per mip by the normal map variance in each texel's footprint (toksvig via a vmf fit), so glossy bumpy surfaces don't sparkle at a distance
    static class SpecularAntiAliasing
    {
        private sealed class Pyramid
        {
            public Pyramid(int width, int height)
            {
                Width = width;
                Height = height;
            }

            public int Width { get; }
            public int Height { get; }
            public float[][] Levels { get; set; }

            public int FindLevel(int width, int height)
            {
                for (int level = 0; level < Levels.Length; level++)
                {
                    if (Math.Max(1, Width >> level) == width && Math.Max(1, Height >> level) == height)
                    {
                        return level;
                    }
                }

                return -1;
            }
        }

        public static Action<int, int, int, byte[]> CreateMipmapProcessor(PixelBuffer mask, PixelBuffer normal)
        {
            if (mask == null || normal == null || mask.Width * normal.Height != normal.Width * mask.Height)
            {
                return null;
            }

            Pyramid roughness = Build(mask.Width, mask.Height, 1, (i, values) =>
            {
                float r = 1.0f - mask.Bytes[i * 4] / 255.0f;
                values[0] = r * r * r * r;
            });

            Pyramid normals = Build(normal.Width, normal.Height, 3, (i, values) =>
            {
                float x = normal.Bytes[i * 4] / 127.5f - 1.0f;
                float y = normal.Bytes[i * 4 + 1] / 127.5f - 1.0f;
                float z = MathF.Sqrt(Math.Max(1.0f - x * x - y * y, 0.0f));
                float length = MathF.Sqrt(x * x + y * y + z * z);
                values[0] = x / length;
                values[1] = y / length;
                values[2] = z / length;
            });

            return (level, width, height, rgba) =>
            {
                int roughnessLevel = roughness.FindLevel(width, height);
                int normalLevel = normals.FindLevel(width, height);

                if (roughnessLevel <= 0 && normalLevel <= 0)
                {
                    return;
                }

                float[] alpha = roughnessLevel > 0 ? roughness.Levels[roughnessLevel] : null;
                float[] average = normalLevel > 0 ? normals.Levels[normalLevel] : null;

                ParallelPixels.For(width * height, (start, end) =>
                {
                    for (int i = start; i < end; i++)
                    {
                        float alpha2;

                        if (alpha != null)
                        {
                            alpha2 = alpha[i];
                        }
                        else
                        {
                            float r = 1.0f - rgba[i * 4] / 255.0f;
                            alpha2 = r * r * r * r;
                        }

                        if (average != null)
                        {
                            float x = average[i * 3], y = average[i * 3 + 1], z = average[i * 3 + 2];
                            float length = Math.Clamp(MathF.Sqrt(x * x + y * y + z * z), 1e-4f, 1.0f);

                            if (length < 0.9999f)
                            {
                                //vmf concentration k = (3r - r^3) / (1 - r^2), slope variance 1/k adds 2/k to alpha^2
                                alpha2 += 2.0f * (1.0f - length * length) / (3.0f * length - length * length * length);
                            }
                        }

                        float gloss = 1.0f - MathF.Pow(Math.Min(alpha2, 1.0f), 0.25f);
                        rgba[i * 4] = (byte)MathF.Round(Math.Clamp(gloss, 0.0f, 1.0f) * 255.0f);
                    }
                });
            };
        }

        private static Pyramid Build(int width, int height, int channels, Action<int, float[]> decode)
        {
            int count = 1;

            while (Math.Max(1, width >> (count - 1)) > 1 || Math.Max(1, height >> (count - 1)) > 1)
            {
                count++;
            }

            Pyramid pyramid = new Pyramid(width, height) { Levels = new float[count][] };

            for (int level = 1; level < count; level++)
            {
                int pw = Math.Max(1, width >> (level - 1)), ph = Math.Max(1, height >> (level - 1));
                int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
                float[] parent = pyramid.Levels[level - 1];
                float[] current = new float[w * h * channels];

                ParallelPixels.For(w * h, (start, end) =>
                {
                    float[] values = new float[channels];

                    for (int i = start; i < end; i++)
                    {
                        int x = i % w, y = i / w;
                        int samples = 0;

                        for (int sy = y * 2; sy < Math.Min(y * 2 + 2, ph); sy++)
                        {
                            for (int sx = x * 2; sx < Math.Min(x * 2 + 2, pw); sx++)
                            {
                                int p = sy * pw + sx;

                                if (parent == null)
                                {
                                    decode(p, values);
                                }
                                else
                                {
                                    Array.Copy(parent, p * channels, values, 0, channels);
                                }

                                for (int c = 0; c < channels; c++)
                                {
                                    current[i * channels + c] += values[c];
                                }

                                samples++;
                            }
                        }

                        for (int c = 0; c < channels; c++)
                        {
                            current[i * channels + c] /= samples;
                        }
                    }
                });

                pyramid.Levels[level] = current;
            }

            return pyramid;
        }
    }
}
