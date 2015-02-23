﻿using System;

namespace TradingStrategy.Strategy
{
    public sealed class PriceAndVolumeChangeFilterMarketEntering
        : GeneralMarketEnteringBase
    {
        private RuntimeMetricProxy _priceChangeMetricProxy;
        private RuntimeMetricProxy _volumeChangeMetricProxy;

        public override string Name
        {
            get { return "价格和成交量变化入市过滤器"; }
        }

        public override string Description
        {
            get { return "当天价格相对上日上涨超过一定比例，成交量超过之前一段时间平均成交量一定比例，且上影线没有超过一定比例则允许入市，否则将不允许入市。"; }
        }

        [Parameter(7.0, "当日价格相对上日收盘价变化的最小幅度百分比")]
        public double MinPriceChangePercentage { get; set; }

        [Parameter(50.0, "当日成交量超过之前一段时间平均成交量的最小幅度百分比")]
        public double MinVolumeChangePercentage { get; set; }

        [Parameter(10, "平均成交量回看窗口")]
        public int VolumeLookbackWindow { get; set; }

        [Parameter(70.0, "上影线最大百分比例")]
        public double MaxPercentageOfUpShadow { get; set; }

        protected override void ValidateParameterValues()
        {
            base.ValidateParameterValues();

            if (MaxPercentageOfUpShadow < 0.0 || MaxPercentageOfUpShadow > 100.0)
            {
                throw new ArgumentOutOfRangeException("MaxPercentageOfUpShadow must be in [0.0..100.0]");
            }

            if (VolumeLookbackWindow <= 0)
            {
                throw new ArgumentOutOfRangeException("VolumeLookbackWindow must be greater than 0");
            }
        }

        public override void Initialize(IEvaluationContext context, System.Collections.Generic.IDictionary<ParameterAttribute, object> parameterValues)
        {
            base.Initialize(context, parameterValues);

            _priceChangeMetricProxy = new RuntimeMetricProxy(Context.MetricManager, "ROC[1]");
            _volumeChangeMetricProxy = new RuntimeMetricProxy(Context.MetricManager, string.Format("VC[{0}]", VolumeLookbackWindow));

        }
        public override bool CanEnter(ITradingObject tradingObject, out string comments, out object obj)
        {
            comments = string.Empty;
            obj = null;

            var bar = Context.GetBarOfTradingObjectForCurrentPeriod(tradingObject);

            var upShadowPercentage = Math.Abs(bar.LowestPrice - bar.HighestPrice) < 1e-6
                ? 0.0
                : (bar.HighestPrice - bar.ClosePrice) / (bar.HighestPrice - bar.LowestPrice) * 100.0;

            var priceChangePercentage = _priceChangeMetricProxy.GetMetricValues(tradingObject)[0];
            var volumeChangePercentage = _volumeChangeMetricProxy.GetMetricValues(tradingObject)[0];

            if (priceChangePercentage >= MinPriceChangePercentage 
                && volumeChangePercentage >= MinVolumeChangePercentage
                && upShadowPercentage <= MaxPercentageOfUpShadow)
            {
                comments = string.Format(
                    "ROC[1]={0:0.000}% VC[{1}]={2:0.000}% UpShadow={3:0.00}%",
                    priceChangePercentage,
                    VolumeLookbackWindow,
                    volumeChangePercentage,
                    upShadowPercentage);

                return true;
            }

            return false;
        }
    }
}
