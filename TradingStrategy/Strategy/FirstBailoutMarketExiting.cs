﻿using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalysis.Share;
using TradingStrategy.Base;

namespace TradingStrategy.Strategy
{
    public sealed class FirstBailoutMarketExiting 
        : GeneralMarketExitingBase
    {
        private readonly PeriodCounter<string> _periodCounter = new PeriodCounter<string>();

        public override string Name
        {
            get { return "首次获利退出"; }
        }

        public override string Description
        {
            get { return "当头寸持有首次获利后退出市场"; }
        }

        [Parameter(0, "价格选择选项。0为最高价，1为最低价，2为收盘价，3为开盘价")]
        public int PriceSelector { get; set; }

        [Parameter(0, "获利后保持周期数")]
        public int KeepPeriods { get; set; }

        protected override void ValidateParameterValues()
        {
 	        base.ValidateParameterValues();

            if (!BarPriceSelector.IsValidSelector(PriceSelector))
            {
                throw new ArgumentException("价格选择项非法");
            }

            if (KeepPeriods < 0)
            {
                throw new ArgumentException("获利后保持周期数非法");
            }
        }

        public override void EvaluateSingleObject(ITradingObject tradingObject, Bar bar)
        {
            base.EvaluateSingleObject(tradingObject, bar);

            if (_periodCounter.Exists(tradingObject))
            {
                _periodCounter.InitializeOrUpdate(tradingObject, 0);
            }
        }

        public override bool ShouldExit(ITradingObject tradingObject, out string comments)
        {
            comments = string.Empty;

            if(!Context.ExistsPosition(tradingObject.Code))
            {
                // remove obseleted data
                _periodCounter.Remove(tradingObject);

                return false;
            }

            if (_periodCounter.Exists(tradingObject))
            {
                if (_periodCounter.GetPeriod(tradingObject, out comments) >= KeepPeriods)
                {
                    _periodCounter.Remove(tradingObject);
                    return true;
                }
            }
            else
            {
                var position = Context.GetPositionDetails(tradingObject.Code).First();
                if (CurrentPeriod <= position.BuyTime)
                {
                    return false;
                }

                var bar = Context.GetBarOfTradingObjectForCurrentPeriod(tradingObject);
                var price = BarPriceSelector.Select(bar, PriceSelector);

                if (position.BuyPrice < price)
                {
                    string tempComments = string.Format("Bailout: buy price {0:0.000}, current price {1:0.000}", position.BuyPrice, price);

                    if (KeepPeriods > 0)
                    {
                        _periodCounter.InitializeOrUpdate(tradingObject, 0, tempComments);

                        return false;
                    }
                    else
                    {
                        comments = tempComments;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}