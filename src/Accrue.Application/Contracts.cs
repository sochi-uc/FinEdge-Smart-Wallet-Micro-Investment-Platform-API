namespace Accrue.Application;

// Requests: nullable members, so missing input is a 400, never a 500. Unknown members are rejected by the API.
public sealed record AmountRequest(decimal? Amount, string? Reference);
public sealed record TransferRequest(string? ToEmail, decimal? Amount, string? Note);
public sealed record InvestRequest(Guid? PlanId, decimal? Amount);
public sealed record PlanRequest(string? Name, string? Description, decimal? AnnualRatePct, decimal? MinAmount, int? LockDays, bool? IsActive);

// Responses
public sealed record WalletDto(Guid AccountId, decimal Balance, string Currency);

public sealed record TransactionDto(Guid Id, DateTimeOffset At, string Type, decimal Amount, decimal BalanceImpact, string Description);

public sealed record PageDto<T>(IReadOnlyList<T> Items, DateTimeOffset? NextBefore);

public sealed record PlanDto(Guid Id, string Name, string Description, decimal AnnualRatePct, decimal MinAmount, int LockDays, bool IsActive);

public sealed record InvestmentDto(
    Guid Id, Guid PlanId, string PlanName, decimal Principal, decimal InterestEarned, decimal Value, decimal AnnualRatePct,
    decimal DailyInterest, DateOnly StartDate, DateOnly MaturesOn, bool CanRedeem, string Status);

public sealed record DashboardDto(
    decimal WalletBalance, decimal Invested, decimal InterestEarned, decimal NetWorth, int ActiveInvestments,
    decimal ProjectedMonthlyInterest, decimal MoneyIn30d, decimal MoneyOut30d, IReadOnlyList<TransactionDto> Recent, DateTimeOffset GeneratedAt);

public sealed record AccrualRunDto(DateOnly Through, int InvestmentsProcessed, int DaysPosted, decimal InterestPaid);
