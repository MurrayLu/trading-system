using System;

namespace MuNTrade.Abstractions.Time;

/// <summary>
/// 表示位於「交易所」時間軸上的時間戳（官方交易所時間）。
/// 用於 UI 顯示、報表、回測對齊，以及 K 線索引。
/// </summary>
public readonly record struct ExchangeTs(DateTime Value) : IComparable<ExchangeTs>, IComparable
{
    public int CompareTo(ExchangeTs other) => Value.CompareTo(other.Value);
    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is ExchangeTs other) return CompareTo(other);
        throw new ArgumentException($"Object must be of type {nameof(ExchangeTs)}");
    }
    public static bool operator <(ExchangeTs left, ExchangeTs right) => left.Value < right.Value;
    public static bool operator >(ExchangeTs left, ExchangeTs right) => left.Value > right.Value;
    public static bool operator <=(ExchangeTs left, ExchangeTs right) => left.Value <= right.Value;
    public static bool operator >=(ExchangeTs left, ExchangeTs right) => left.Value >= right.Value;
    public static TimeSpan operator -(ExchangeTs left, ExchangeTs right) => left.Value - right.Value;
    public static ExchangeTs operator +(ExchangeTs left, TimeSpan right) => new(left.Value + right);
    public static ExchangeTs operator -(ExchangeTs left, TimeSpan right) => new(left.Value - right);
    public TimeSpan Subtract(ExchangeTs value) => Value.Subtract(value.Value);
    public ExchangeTs Subtract(TimeSpan value) => new(Value.Subtract(value));
    public long Ticks => Value.Ticks;
    public DateTime ToDateTime() => Value;
    public override string ToString() => Value.ToString("HH:mm:ss.fff");
}

/// <summary>
/// 表示位於「到達／券商」時間軸上的時間戳（券商本地時間）。
/// 用於視窗化（Windowing）、持續時間（Duration）、排序（Ordering）、密度（Density）、延遲（Latency），以及市場微結構（Microstructure）評分。
/// </summary>
public readonly record struct ArrivalTs(DateTime Value) : IComparable<ArrivalTs>, IComparable
{
    public int CompareTo(ArrivalTs other) => Value.CompareTo(other.Value);
    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is ArrivalTs other) return CompareTo(other);
        throw new ArgumentException($"Object must be of type {nameof(ArrivalTs)}");
    }
    public static bool operator <(ArrivalTs left, ArrivalTs right) => left.Value < right.Value;
    public static bool operator >(ArrivalTs left, ArrivalTs right) => left.Value > right.Value;
    public static bool operator <=(ArrivalTs left, ArrivalTs right) => left.Value <= right.Value;
    public static bool operator >=(ArrivalTs left, ArrivalTs right) => left.Value >= right.Value;
    public static TimeSpan operator -(ArrivalTs left, ArrivalTs right) => left.Value - right.Value;
    public static ArrivalTs operator +(ArrivalTs left, TimeSpan right) => new(left.Value + right);
    public static ArrivalTs operator -(ArrivalTs left, TimeSpan right) => new(left.Value - right);
    public TimeSpan Subtract(ArrivalTs value) => Value.Subtract(value.Value);
    public ArrivalTs Subtract(TimeSpan value) => new(Value.Subtract(value));
    public long Ticks => Value.Ticks;
    public DateTime ToDateTime() => Value;
    public override string ToString() => Value.ToString("HH:mm:ss.fff");
}

