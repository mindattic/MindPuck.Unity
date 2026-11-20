// Port of MindPuck.Web/src/core/rng.ts.
namespace MindPuck.Core
{
    /// <summary>
    /// mulberry32: a tiny 32-bit generator. It uses only 32-bit integer arithmetic, so this port
    /// produces the same sequence as the TypeScript original.
    /// </summary>
    public sealed class Rng
    {
        private uint a;

        public Rng(uint seed)
        {
            a = seed;
        }

        /// <summary>The next value in [0, 1). The same as calling the function mulberry32 returns in TS.</summary>
        public double Next()
        {
            unchecked
            {
                a += 0x6d2b79f5u;
                uint t = a;
                t = (t ^ (t >> 15)) * (t | 1u);
                t ^= t + (t ^ (t >> 7)) * (t | 61u);
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }

        public static Rng Mulberry32(uint seed)
        {
            return new Rng(seed);
        }

        /// <summary>Mix a seed, a level number and an attempt number into one 32-bit seed.</summary>
        public static uint MixSeed(uint seed, int index, int attempt)
        {
            unchecked
            {
                return (seed * 73856093u) ^ ((uint)index * 19349663u) ^ ((uint)attempt * 83492791u);
            }
        }

        public static double Range(Rng rng, double lo, double hi)
        {
            return lo + (hi - lo) * rng.Next();
        }

        /// <summary>Roughly normal with unit variance: the sum of four uniforms, rescaled. Cheap and portable.</summary>
        public static double Gaussian(Rng rng)
        {
            // Four separate statements keep the order of the draws, and of the additions, the same as in TS.
            double s = rng.Next();
            s += rng.Next();
            s += rng.Next();
            s += rng.Next();
            return (s - 2) * 1.7320508075688772;
        }
    }
}
