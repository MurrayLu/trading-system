namespace MuNTrade.Abstractions.Dtos.Capital
{
    /// <summary>
    /// 證券一戶通資金查詢回傳資料。
    /// </summary>
    public sealed record OneAccountBalanceDto
    {
        /// <summary>是否已開通一戶通。</summary>
        public bool IsOpen { get; init; }

        /// <summary>餘額。</summary>
        public decimal Balance { get; init; }

        /// <summary>可用。</summary>
        public decimal Available { get; init; }

        /// <summary>可出（可出金）。</summary>
        public decimal Withdrawable { get; init; }
    }
}


