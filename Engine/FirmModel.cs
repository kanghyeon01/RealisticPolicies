using Unity.Mathematics;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// Aggregate industrial abatement model for the environment bundle (docs/PLAN.md §10.5).
    /// Each company has an abatement cost coefficient c (log-normal across companies); cutting a share r of its
    /// emissions costs c·r² (rising marginal cost). Instead of walking companies, the distribution is represented by
    /// fixed quantiles, so every function here is a short scalar loop.
    /// Units: c is relative to the median company (median 1); a price t is per unit of emissions in the same units,
    /// so a company facing price t cuts r = t / 2c (marginal cost 2cr equals the price).
    /// </summary>
    public static class FirmModel
    {
        private const int kQuantiles = 32;

        /// <summary>Spread of log abatement costs across companies.</summary>
        public const float kCostSigma = 0.8f;

        /// <summary>Price at which the median company cuts 30% (the bundle's base tax and subsidy rate).</summary>
        public const float kBasePrice = 0.6f;

        private static readonly float[] s_Costs = BuildCosts();

        /// <summary>Average c across companies.</summary>
        public static readonly float MeanCost = Mean(s_Costs);

        private static float Mean(float[] values)
        {
            float sum = 0f;
            foreach (float value in values)
            {
                sum += value;
            }
            return sum / values.Length;
        }

        private static float[] BuildCosts()
        {
            var costs = new float[kQuantiles];
            for (int k = 0; k < kQuantiles; k++)
            {
                float p = (k + 0.5f) / kQuantiles;
                costs[k] = math.exp(kCostSigma * InverseNormal(p));
            }
            return costs;
        }

        /// <summary>Average share of emissions cut when every company faces price t (Pigouvian tax or abatement subsidy).</summary>
        public static float Abatement(float price)
        {
            if (price <= 0f)
            {
                return 0f;
            }
            float sum = 0f;
            for (int k = 0; k < kQuantiles; k++)
            {
                sum += math.min(1f, price / (2f * s_Costs[k]));
            }
            return sum / kQuantiles;
        }

        /// <summary>Average abatement cost c·r² per company at price t, in the same units as the price.</summary>
        public static float AbatementCost(float price)
        {
            if (price <= 0f)
            {
                return 0f;
            }
            float sum = 0f;
            for (int k = 0; k < kQuantiles; k++)
            {
                float r = math.min(1f, price / (2f * s_Costs[k]));
                sum += s_Costs[k] * r * r;
            }
            return sum / kQuantiles;
        }

        /// <summary>
        /// Share of companies for which meeting a cut r costs less than the expected fine (Becker 1968):
        /// c·r² ≤ detection × fine.
        /// </summary>
        public static float DeterredShare(float cut, float detection, float fine)
        {
            if (cut <= 0f)
            {
                return 1f;
            }
            float limit = detection * fine / (cut * cut);
            int count = 0;
            for (int k = 0; k < kQuantiles; k++)
            {
                if (s_Costs[k] <= limit)
                {
                    count++;
                }
            }
            return (float)count / kQuantiles;
        }

        /// <summary>Average cost c·r² of companies that meet a uniform cut r.</summary>
        public static float StandardCost(float cut) => cut * cut * MeanCost;

        /// <summary>Acklam's rational approximation of the standard normal quantile (good to ~1e-9).</summary>
        private static float InverseNormal(float p)
        {
            double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
            double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
            double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
            double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
            const double low = 0.02425;
            double q;
            if (p < low)
            {
                q = System.Math.Sqrt(-2 * System.Math.Log(p));
                return (float)((((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1));
            }
            if (p > 1 - low)
            {
                q = System.Math.Sqrt(-2 * System.Math.Log(1 - p));
                return (float)-((((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1));
            }
            q = p - 0.5;
            double r = q * q;
            return (float)((((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1));
        }
    }
}
