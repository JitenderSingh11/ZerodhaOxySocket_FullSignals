using System;
using System.Collections.Generic;
using System.Linq;

namespace ZerodhaOxySocket.Helpers
{
    public class EmpiricalDeltaEstimator
    {
        private readonly int _windowSize;
        private readonly LinkedList<(double s, double o)> _buf = new();

        public EmpiricalDeltaEstimator(int windowSize = 50)
        {
            _windowSize = windowSize;
        }

        public void AddSample(double underlyingPrice, double optionPrice)
        {
            _buf.AddLast((underlyingPrice, optionPrice));
            if (_buf.Count > _windowSize) _buf.RemoveFirst();
        }

        public double? GetDelta()
        {
            if (_buf.Count < 3) return null;
            var arr = _buf.ToArray();
            var ratios = new List<double>();
            for (int i = 1; i < arr.Length; i++)
            {
                double ds = arr[i].s - arr[i - 1].s;
                double doo = arr[i].o - arr[i - 1].o;
                if (Math.Abs(ds) < 1e-8) continue;
                ratios.Add(doo / ds);
            }
            if (ratios.Count == 0) return null;
            ratios.Sort();
            return ratios[ratios.Count / 2];
        }
    }
}
