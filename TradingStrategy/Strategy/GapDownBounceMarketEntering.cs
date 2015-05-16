﻿using System;
using System.Collections.Generic;
using System.Linq;
using TradingStrategy.Base;

namespace TradingStrategy.Strategy
{
    public sealed class GapDownBounceMarketEntering
        : GeneralMarketEnteringBase
    {
        private const int LowestPeriod = 5;

        private RuntimeMetricProxy _movingAverage;
        private RuntimeMetricProxy _referenceBar;

        [Parameter(5, "移动平均趋势线周期")]
        public int MovingAveragePeriod { get; set; }

        [Parameter(4.0, "收盘价应当比移动平均低的最小百分比例")]
        public double MinPercentageBelowMovingAverage { get; set; }

        [Parameter(1.0, "跳空开盘价低于昨日最低价的最小百分比")]
        public double MinPercentageOfGapDown { get; set; }

        [Parameter(0.0, "反弹超过昨日最低价的最小百分比")]
        public double MinBouncePercentageOverLastLowestPrice { get; set; }

        [Parameter(50.0, "上影线所允许的最大比例")]
        public double MaxUpShadowPercentage { get; set; }

        [Parameter(50.0, "下影线所允许的最大比例")]
        public double MaxDownShadowPercentage { get; set; }

        protected override void RegisterMetric()
        {
            base.RegisterMetric();

            _movingAverage = new RuntimeMetricProxy(
                Context.MetricManager,
                string.Format("MA[{0}]", MovingAveragePeriod));

            _referenceBar = new RuntimeMetricProxy(
                Context.MetricManager,
                "REFBAR[1]");
        }

        public override string Name
        {
            get { return "跳空后反弹入市策略"; }
        }

        public override string Description
        {
            get { return "当收盘价低于移动平均一定程度，并且开盘跳空，收盘超过昨日收盘后入市"; }
        }

        public override bool CanEnter(ITradingObject tradingObject, out string comments, out object obj)
        {
            comments = string.Empty;
            obj = null;

            var todayBar = Context.GetBarOfTradingObjectForCurrentPeriod(tradingObject);
            var lastBarValues = _referenceBar.GetMetricValues(tradingObject);
            var movingAverage = _movingAverage.GetMetricValues(tradingObject)[0];
            var lastBarLowest = lastBarValues[3];

            var upShadowPercentage = Math.Abs(todayBar.HighestPrice - todayBar.LowestPrice) < 1e-6
                ? 100.0
                : (todayBar.HighestPrice - todayBar.ClosePrice) / (todayBar.HighestPrice - todayBar.LowestPrice) * 100.0;

            var downShadowPercentage = Math.Abs(todayBar.HighestPrice - todayBar.LowestPrice) < 1e-6
                ? 0.0
                : (todayBar.OpenPrice - todayBar.LowestPrice) / (todayBar.HighestPrice - todayBar.LowestPrice) * 100.0;

            if (todayBar.ClosePrice < movingAverage * (100.0 - MinPercentageBelowMovingAverage) / 100.0 // below average
                && todayBar.OpenPrice < lastBarLowest * (100.0 - MinPercentageOfGapDown) / 100.0 // gap down
                && todayBar.ClosePrice > lastBarLowest * (100.0 + MinBouncePercentageOverLastLowestPrice) / 100.0 // bounce over last day
                && upShadowPercentage <= MaxUpShadowPercentage
                && downShadowPercentage <= MaxDownShadowPercentage
                )
            { 
                comments += string.Format(
                    "MA[{0}]={1:0.000} Close:{2:0.000} Open:{3:0.000} LastLowest:{4:0.000} UpShadow%:{5:0.000}%", 
                    MovingAveragePeriod,
                    movingAverage,
                    todayBar.ClosePrice,
                    todayBar.OpenPrice,
                    lastBarLowest,
                    upShadowPercentage);

                return true;
            }

            return false;
        }
    }
}