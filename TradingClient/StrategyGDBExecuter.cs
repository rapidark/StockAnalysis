﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using StockTrading.Utility;
using StockAnalysis.Share;
using System.Threading;

namespace TradingClient
{
    sealed class StrategyGdbExecuter
    {
        private const int MaxBuyCount = 3;
        private const string DataFileFolder = "StrategyGDB";
        private readonly TimeSpan _startRunTime = new TimeSpan(9, 29, 0);
        private readonly TimeSpan _endRunTime = new TimeSpan(14, 50, 0);
        private readonly TimeSpan _startExecuteTime = new TimeSpan(9, 29, 0);
        private readonly TimeSpan _endExecuteTime = new TimeSpan(15, 30, 0);
        private readonly TimeSpan _startAcceptQuoteTime = new TimeSpan(9, 30, 0);
        private readonly TimeSpan _endAcceptQuoteTime = new TimeSpan(14, 56, 0);
        private readonly TimeSpan _startPublishStoplossOrderTime = new TimeSpan(9, 30, 0);
        private readonly TimeSpan _endPublishStoplossOrderTime = new TimeSpan(14, 56, 0);
        private readonly TimeSpan _startPublishBuyOrderTime = new TimeSpan(9, 30, 0);
        private readonly TimeSpan _endPublishBuyOrderTime = new TimeSpan(14, 55, 0);
        private readonly TimeSpan _startPublishSellOrderTime = new TimeSpan(14, 55, 0);
        private readonly TimeSpan _endPublishSellOrderTime = new TimeSpan(14, 57, 0);

        // the new stock has been bought
        private HashSet<StrategyGDB.NewStock> _boughtStock = new HashSet<StrategyGDB.NewStock>();

        private List<StrategyGDB.NewStock> _newStocks = null;
        private List<StrategyGDB.ExistingStock> _existingStocks = null;
        
        private HashSet<string> _allCodes = null;
        private Dictionary<string, StrategyGDB.NewStock> _activeNewStockIndex = null;
        private Dictionary<string, StrategyGDB.ExistingStock> _activeExistingStockIndex = null;

        private Dictionary<object, StockOrderRuntime> _stockOrderRuntimes = new Dictionary<object, StockOrderRuntime>();

        private ReaderWriterLockSlim _runtimeReadWriteLock = new ReaderWriterLockSlim();

        private float _useableCapital = 0.0f;
        private object _queryCapitalLockObj = new object();

        public IEnumerable<StockOrderRuntime> StockOrderRuntimes
        {
            get 
            {
                _runtimeReadWriteLock.EnterReadLock();

                try
                {
                    return new List<StockOrderRuntime>(_stockOrderRuntimes.Values);
                }
                finally
                {
                    _runtimeReadWriteLock.ExitReadLock();
                }
            }
        }

        public StrategyGdbExecuter()
        {
            Initialize();
        }

        private void Initialize()
        {
            StrategyGDB.DataFileReaderWriter rw = new StrategyGDB.DataFileReaderWriter(DataFileFolder);

            rw.Read();

            _newStocks = rw.NewStocks.ToList();
            _existingStocks = rw.ExistingStocks.ToList();

            var allCodes = _newStocks
                .Select(n => n.SecurityCode)
                .Union(_existingStocks.Select(e => e.SecurityCode))
                .Distinct();
                

            _allCodes = new HashSet<string>(allCodes);

            if (AppLogger.Default.IsDebugEnabled)
            {
                AppLogger.Default.DebugFormat(
                    "GDB strategy executer: loaded codes {0}",
                    string.Join(",", _allCodes));
            }

            _activeNewStockIndex = _newStocks.ToDictionary(s => s.SecurityCode, s => s);
            _activeExistingStockIndex = _existingStocks.ToDictionary(s => s.SecurityCode, s => s);
        }

        public void Run()
        {
            if (_allCodes.Count == 0)
            {
                return;
            }

            if (!WaitForActionTime(_startRunTime, _endRunTime))
            {
                AppLogger.Default.ErrorFormat("Wait for valid trading time failed");
                return;
            }

            // update useable capital
            UpdateCurrentUseableCapital();

            // subscribe quote for all stocks
            CtpSimulator.GetInstance().SubscribeQuote(_newStocks.Select(s => new QuoteSubscription(s.SecurityCode, OnNewStockQuoteReady)));
            AppLogger.Default.InfoFormat(
                "Subscribe quote {0}", 
                string.Join(",",_newStocks.Select(s => s.SecurityCode)));

            CtpSimulator.GetInstance().SubscribeQuote(_existingStocks.Select(s => new QuoteSubscription(s.SecurityCode, OnExistingStockQuoteReady)));
            AppLogger.Default.InfoFormat(
                "Subscribe quote {0}",
                string.Join(",", _existingStocks.Select(s => s.SecurityCode)));

            while (IsValidActionTime(_startExecuteTime, _endExecuteTime))
            {
                Stoploss();
                Sell();

                Thread.Sleep(1000);
            }
        }

        private void UpdateCurrentUseableCapital()
        {
            lock (_queryCapitalLockObj)
            {
                string error;

                QueryCapitalResult result = CtpSimulator.GetInstance().QueryCapital(out error);

                float useableCapital = 0.0f;

                if (result == null)
                {
                    AppLogger.Default.ErrorFormat("Failed to query capital. Error: {0}", error);
                }
                else
                {
                    useableCapital = result.UsableCapital;
                }

                this._useableCapital = useableCapital;
            }
        }

        private bool WaitForActionTime(TimeSpan startTime, TimeSpan endTime)
        {
            do
            {
                TimeSpan now = DateTime.Now.TimeOfDay;

                if (now > endTime)
                {
                    return false;
                }

                if (now >= startTime && now <= endTime)
                {
                    return true;
                }

                System.Threading.Thread.Sleep(1000);
            } while (true);
        }

        private bool IsValidActionTime(TimeSpan startTime, TimeSpan endTime)
        {
            TimeSpan now = DateTime.Now.TimeOfDay;

            return IsValidActionTime(now, startTime, endTime);
        }

        private bool IsValidActionTime(TimeSpan currentTime, TimeSpan startTime, TimeSpan endTime)
        {
            return (currentTime >= startTime && currentTime <= endTime);
        }

        private void UnsubscribeNewStock(string code)
        {
            CtpSimulator.GetInstance().UnsubscribeQuote(new QuoteSubscription(code, OnNewStockQuoteReady));
            AppLogger.Default.InfoFormat("Unsubscribe quote {0}", code);
        }

        private void UnsubscribeExistingStock(string code)
        {
            CtpSimulator.GetInstance().UnsubscribeQuote(new QuoteSubscription(code, OnExistingStockQuoteReady));
            AppLogger.Default.InfoFormat("Unsubscribe quote {0}", code);
        }

        private void Buy(FiveLevelQuote quote)
        {
            _runtimeReadWriteLock.EnterWriteLock();

            try
            {
                if (!IsValidActionTime(quote.Timestamp.TimeOfDay, _startAcceptQuoteTime, _endAcceptQuoteTime))
                {
                    return;
                }

                if (!_activeNewStockIndex.ContainsKey(quote.SecurityCode))
                {
                    UnsubscribeNewStock(quote.SecurityCode);
                    return;
                }

                var stock = _activeNewStockIndex[quote.SecurityCode];

                // check if buy order has been created
                if (_stockOrderRuntimes.ContainsKey(stock))
                {
                    var runtime = _stockOrderRuntimes[stock];
                    if (runtime.AssociatedBuyOrder != null)
                    {
                        return;
                    }
                }

                lock (_boughtStock)
                {
                    if (_boughtStock.Count >= StrategyGdbExecuter.MaxBuyCount)
                    {
                        UnsubscribeNewStock(quote.SecurityCode);
                        return;
                    }
                }

                if (stock.DateToBuy.Date != DateTime.Today)
                {
                    // remove from active new stock
                    _activeNewStockIndex.Remove(quote.SecurityCode);

                    AppLogger.Default.WarnFormat(
                        "The buy date of stock {0:yyyy-MM-dd} is not today. {1}/{2}",
                        stock.DateToBuy,
                        stock.SecurityCode,
                        stock.SecurityName);

                    return;
                }

                // determine if open price is in valid range
                if (float.IsNaN(stock.ActualOpenPrice))
                {
                    double upLimitPrice = ChinaStockHelper.CalculatePrice(
                        quote.YesterdayClosePrice,
                        stock.OpenPriceUpLimitPercentage - 100.0f,
                        2);

                    double downLimitPrice = ChinaStockHelper.CalculatePrice(
                        quote.YesterdayClosePrice,
                        stock.OpenPriceDownLimitPercentage - 100.0f,
                        2);

                    if (quote.TodayOpenPrice < downLimitPrice
                        || quote.TodayOpenPrice > upLimitPrice
                        || quote.TodayOpenPrice < stock.StoplossPrice)
                    {
                        // remove from active new stock
                        _activeNewStockIndex.Remove(quote.SecurityCode);

                        AppLogger.Default.InfoFormat(
                            "Failed to buy stock because open price is out of range. {0}/{1} open {2:0.000} out of [{3:0.000}, {4:0.000}]",
                            stock.SecurityCode,
                            stock.SecurityName,
                            quote.TodayOpenPrice,
                            downLimitPrice,
                            upLimitPrice);
                    }
                    else
                    {
                        stock.ActualOpenPrice = quote.TodayOpenPrice;
                        stock.ActualOpenPriceDownLimit = (float)downLimitPrice;
                        stock.ActualOpenPriceUpLimit = (float)upLimitPrice;
                        stock.ActualMaxBuyPrice = (float)ChinaStockHelper.CalculatePrice(
                            quote.TodayOpenPrice,
                            stock.MaxBuyPriceIncreasePercentage,
                            2);
                        stock.TodayDownLimitPrice = (float)ChinaStockHelper.CalculateDownLimit(stock.SecurityCode, stock.SecurityName, quote.YesterdayClosePrice, 2);
                        stock.ActualMinBuyPrice = Math.Max(stock.StoplossPrice, stock.TodayDownLimitPrice);

                    }
                }

                // only buy those stock which has been raised over open price
                if (quote.CurrentPrice > quote.TodayOpenPrice)
                {
                    stock.IsBuyable = true;
                }

                if (stock.IsBuyable)
                {
                    if (IsValidActionTime(quote.Timestamp.TimeOfDay, _startPublishBuyOrderTime, _endPublishBuyOrderTime))
                    {
                        CreateBuyOrder(stock);

                        // unsubscribe the quote because all conditions has been setup.
                        UnsubscribeNewStock(quote.SecurityCode);
                    }
                }
            }
            finally
            {
                _runtimeReadWriteLock.ExitWriteLock();
            }
        }

        private void CreateBuyOrder(StrategyGDB.NewStock stock)
        {
            if (_stockOrderRuntimes.ContainsKey(stock))
            {
                if (_stockOrderRuntimes[stock].AssociatedBuyOrder != null)
                {
                    return;
                }
            }

            // update usable capital before issuing any buy order
            UpdateCurrentUseableCapital();

            if (_useableCapital < stock.TotalCapital * 0.9)
            {
                return;
            }

            float capital = Math.Min(stock.TotalCapital, _useableCapital);

            int maxVolume = (int)(capital / stock.ActualMaxBuyPrice);

            BuyInstruction instruction = new BuyInstruction(
                stock.SecurityCode,
                stock.SecurityName,
                stock.ActualMinBuyPrice,
                stock.ActualMaxBuyPrice,
                stock.ActualMaxBuyPrice,
                capital,
                maxVolume);

            BuyOrder order = new BuyOrder(instruction, OnBuyOrderExecuted);

            if (_stockOrderRuntimes.ContainsKey(stock))
            {
                _stockOrderRuntimes[stock].AssociatedBuyOrder = order;
            }
            else
            {
                var runtime = new StockOrderRuntime(stock.SecurityCode, stock.SecurityName)
                    {
                        ExpectedVolume = order.ExpectedVolume,
                        RemainingVolume = order.ExpectedVolume,
                        AssociatedBuyOrder = order,
                    };

                _stockOrderRuntimes.Add(stock, runtime);
            }

            OrderManager.GetInstance().RegisterOrder(order);

            AppLogger.Default.InfoFormat("Registered order {0}", order);
        }

        private void OnBuyOrderExecuted(IOrder order, float dealPrice, int dealVolume)
        {
            AppLogger.Default.InfoFormat("Order executed. order details: {0}", order);

            if (dealVolume <= 0)
            {
                return;
            }

            HashSet<StrategyGDB.NewStock> boughtStockCopy = null;

            lock (_boughtStock)
            {
                if (_boughtStock.Count >= StrategyGdbExecuter.MaxBuyCount)
                {
                    return;
                }
                 
                _boughtStock.Add(_activeNewStockIndex[order.SecurityCode]);

                if (_boughtStock.Count < StrategyGdbExecuter.MaxBuyCount)
                {
                    return;
                }

                // create a copy to avoid deadlock between _boughtStock's lock and _runtimeReadWriteLock;
                boughtStockCopy = new HashSet<StrategyGDB.NewStock>(_boughtStock);
            }

            // remove all other buy orders which has not been executed successfully asynchronously
            // to avoid deadlock in OrderManager.
            Action action = () =>
            {
                _runtimeReadWriteLock.EnterWriteLock();

                try
                {
                    foreach (var kvp in _stockOrderRuntimes)
                    {
                        if (kvp.Value.AssociatedBuyOrder != null)
                        {
                            if (!boughtStockCopy.Contains(kvp.Key))
                            {
                                OrderManager.GetInstance().UnregisterOrder(kvp.Value.AssociatedBuyOrder);
                                kvp.Value.AssociatedBuyOrder = null;
                            }
                        }
                    }
                }
                finally
                {
                    _runtimeReadWriteLock.ExitWriteLock();
                }
            };

            Task.Run(action);
        }

        private void Stoploss()
        {
            if (!IsValidActionTime(_startPublishStoplossOrderTime, _endPublishStoplossOrderTime))
            {
                return;
            }

            _runtimeReadWriteLock.EnterWriteLock();

            try
            {
                foreach (var stock in _activeExistingStockIndex.Values)
                {
                    if (!_stockOrderRuntimes.ContainsKey(stock))
                    {
                        StoplossOrder order = new StoplossOrder(
                            stock.SecurityCode,
                            stock.SecurityName,
                            stock.StoplossPrice,
                            stock.Volume,
                            OnStoplossOrderExecuted);

                        StockOrderRuntime runtime = new StockOrderRuntime(stock.SecurityCode, stock.SecurityName)
                        {
                            AssociatedStoplossOrder = order,
                            ExpectedVolume = stock.Volume,
                            RemainingVolume = stock.Volume,
                        };

                        _stockOrderRuntimes.Add(stock, runtime);

                        OrderManager.GetInstance().RegisterOrder(order);
                        AppLogger.Default.InfoFormat("Registered order {0}", order);
                    }
                    else
                    {
                        var runtime = _stockOrderRuntimes[stock];
                        if (runtime.AssociatedSellOrder == null && runtime.AssociatedStoplossOrder == null)
                        {
                            StoplossOrder order = new StoplossOrder(
                                stock.SecurityCode,
                                stock.SecurityName,
                                stock.StoplossPrice,
                                runtime.RemainingVolume,
                                OnStoplossOrderExecuted);

                            runtime.AssociatedStoplossOrder = order;

                            OrderManager.GetInstance().RegisterOrder(order);
                            AppLogger.Default.InfoFormat("Registered order {0}", order);
                        }
                    }
                }
            }
            finally
            {
                _runtimeReadWriteLock.ExitWriteLock();
            }
        }

        private void OnStoplossOrderExecuted(IOrder order, float dealPrice, int dealVolume)
        {
            AppLogger.Default.InfoFormat("Order executed. order details: {0}", order);

            if (dealVolume <= 0)
            {
                return;
            }

            _runtimeReadWriteLock.EnterReadLock();
            try
            {
                var stock = _activeExistingStockIndex[order.SecurityCode];
                System.Diagnostics.Debug.Assert(stock != null);

                var runtime = _stockOrderRuntimes[stock];
                System.Diagnostics.Debug.Assert(runtime != null);
                System.Diagnostics.Debug.Assert(object.ReferenceEquals(runtime.AssociatedStoplossOrder, order));

                runtime.RemainingVolume -= dealVolume;
                System.Diagnostics.Debug.Assert(runtime.RemainingVolume >= 0);
            }
            finally
            {
                _runtimeReadWriteLock.ExitReadLock();
            }
        }

        private void Sell()
        {

        }
        private void OnSellOrderExecuted(IOrder order, float dealPrice, int dealVolume)
        {
            AppLogger.Default.InfoFormat("Order executed. order details: {0}", order);
        }

        private void OnNewStockQuoteReady(IEnumerable<QuoteResult> quotes)
        {
            foreach (var quote in quotes)
            {
                if (!quote.IsValidQuote())
                {
                    continue;
                }

                Buy(quote.Quote);
            }
        }

        private void OnExistingStockQuoteReady(IEnumerable<QuoteResult> quotes)
        {
            foreach (var quote in quotes)
            {
                if (!quote.IsValidQuote())
                {
                    continue;
                }

                if (!_activeExistingStockIndex.ContainsKey(quote.SecurityCode))
                {
                    CtpSimulator.GetInstance().UnsubscribeQuote(new QuoteSubscription(quote.SecurityCode, OnExistingStockQuoteReady));

                    AppLogger.Default.InfoFormat("Unsubscribe quote {0}", quote.SecurityCode);
                    continue;
                }

                FiveLevelQuote currentQuote = quote.Quote;

                // check if we can issue buy order
                if (_activeNewStockIndex.ContainsKey(currentQuote.SecurityCode))
                {
                    Buy(currentQuote);
                }

                // check if we can issue sell order
                if (_activeExistingStockIndex.ContainsKey(currentQuote.SecurityCode))
                {
                    TryPublishSellOrder(currentQuote);
                }
            }
        }

        private void TryPublishSellOrder(FiveLevelQuote quote)
        {
            var stock = _activeExistingStockIndex[quote.SecurityCode];

            // for sell order, if current price is up limit, sell it immediately
            float upLimitPrice = (float)ChinaStockHelper.CalculateUpLimit(
                    quote.SecurityCode, 
                    quote.SecurityName, 
                    quote.YesterdayClosePrice, 
                    2);

            if (Math.Abs(quote.CurrentPrice - upLimitPrice) < 0.001 // reach up limit
                && Math.Abs(quote.TodayOpenPrice - upLimitPrice) > 0.001 // not 一字板
                && stock.HoldDays > 1)
            {
                // remove stop loss order if have
               // var stoplossOrder = _stoplossOrders.FirstOrDefault(s => s.SecurityCode == stock.SecurityCode);
                //if (stoplossOrder != default(StoplossOrder))
                {
                    //_stoplossOrders.Remove(stoplossOrder);

                    //OrderManager.GetInstance().UnregisterOrder(stoplossOrder);

                    // need to add back to existing stock index
                    // TODO
                    // TODO
                    // TODO
                    // TODO
                }

                SellOrder order = new SellOrder(
                    stock.SecurityCode, 
                    stock.SecurityName, 
                    upLimitPrice, 
                    stock.Volume,
                    OnSellOrderExecuted);

                //_sellOrders.Add(order);

                OrderManager.GetInstance().RegisterOrder(order);

                _activeExistingStockIndex.Remove(stock.SecurityCode);

                AppLogger.Default.InfoFormat(
                    "Sell on up limit. Id: {0}, {1}/{2} price {3:0.000}",
                    order.OrderId,
                    order.SecurityCode,
                    order.SecurityName,
                    order.SellPrice);
            }
                
            if (!IsValidActionTime(quote.Timestamp.TimeOfDay, _startPublishSellOrderTime, _endPublishSellOrderTime))
            {
                return;
            }

            // check if sell order 
            
        }


    }
}
