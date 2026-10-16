using AshkanQuantEdge.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AshkanQuantEdge;

/// <summary>Transaction-owned SQL application lock shared by all SQL fill paths.</summary>
public static class SqlAccountLock
{
    public static async Task<bool> AcquireAsync(TradingDbContext db, Guid accountId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Begin a SQL transaction before acquiring an account lock.");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=1000; SELECT @r";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = "QE_PAPER_ACCOUNT_" + accountId.ToString("N");
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) >= 0;
    }
}
