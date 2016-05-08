﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StockTrading.Utility
{
    public sealed class QuoteResult
    {
        public string SecurityCode { get; private set; }

        public string Error { get; private set; }

        public FiveLevelQuote Quote { get; private set; }

        public QuoteResult(string code, FiveLevelQuote quote, string error)
        {
            SecurityCode = code;
            Quote = quote;
            Error = error;
        }

        public bool IsValidQuote()
        {
            return string.IsNullOrEmpty(Error);
        }
    }
}