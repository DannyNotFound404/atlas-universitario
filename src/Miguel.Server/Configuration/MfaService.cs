using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Miguel.Server.Admin;
using Miguel.Server.Reports;
using Npgsql;

namespace Miguel.Server.Configuration;

public sealed record MfaRequest(string Password,string? Code=null);
public sealed class MfaService(NpgsqlDataSource db,IDataProtectionProvider protection,AdminRepository accounts,
    IPasswordHasher<AdminUserAccount> hasher,TimeProvider clock)
{
    private readonly IDataProtector protector=protection.CreateProtector("ATLAS.Admin.MFA.v1");
    public static string Totp(byte[] secret,long step,int digits=6)
    {
        Span<byte> counter=stackalloc byte[8];BinaryPrimitives.WriteInt64BigEndian(counter,step);
        byte[] hash=HMACSHA1.HashData(secret,counter);int offset=hash[^1]&15;
        uint value=BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(offset,4))&0x7fffffff;
        return (value%(digits==8?100000000u:1000000u)).ToString(digits==8?"D8":"D6",CultureInfo.InvariantCulture);
    }
    public static string Base32(byte[] bytes)
    {
        const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";StringBuilder b=new();int bits=0,value=0;
        foreach(byte x in bytes){value=(value<<8)|x;bits+=8;while(bits>=5){b.Append(alphabet[(value>>(bits-5))&31]);bits-=5;}}
        if(bits>0)b.Append(alphabet[(value<<(5-bits))&31]);return b.ToString();
    }
    private static bool Equal(string a,string b)=>CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a),Encoding.UTF8.GetBytes(b));
    public async Task<bool> EnabledAsync(Guid user,CancellationToken ct)
    {await using var cmd=db.CreateCommand("SELECT enabled FROM atlas_admin_mfa WHERE user_id=$1");cmd.Parameters.AddWithValue(user);return await cmd.ExecuteScalarAsync(ct) is true;}
    private async Task<AdminUserAccount> ReauthenticateAsync(string actor,string password,CancellationToken ct)
    {
        var account=await accounts.FindAdminAsync(AdminAccountService.NormalizeUsername(actor),ct);
        if(account is null || !account.Enabled || account.LockedUntilUtc>clock.GetUtcNow())throw new PortalException("No se pudo confirmar su identidad.",401);
        if(password is null || password.Length>128 || hasher.VerifyHashedPassword(account,account.PasswordHash,password)==PasswordVerificationResult.Failed)
        {await FailAsync(account,ct);throw new PortalException("No se pudo confirmar su identidad.",401);}return account;
    }
    private async Task FailAsync(AdminUserAccount account,CancellationToken ct)
    {
        await accounts.RecordFailedLoginAsync(account.Id,5,clock.GetUtcNow().AddMinutes(15),ct);
        await accounts.WriteAuditAsync(clock.GetUtcNow(),account.Role,account.Username,"admin.mfa.verification.failed","AdminUser",account.Id.ToString(),new{secretStoredInAudit=false},ct);
    }
    private static async Task FenceAsync(NpgsqlConnection c,NpgsqlTransaction t,AdminUserAccount account,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("SELECT id FROM admin_users WHERE id=$1 AND enabled AND password_hash=$2 AND security_revision=$3 FOR UPDATE",c,t);
        cmd.Parameters.AddWithValue(account.Id);cmd.Parameters.AddWithValue(account.PasswordHash);cmd.Parameters.AddWithValue(account.SecurityRevision);
        if(await cmd.ExecuteScalarAsync(ct) is not Guid)throw new PortalException("La cuenta cambió. Inicie sesión nuevamente.",401);
    }
    public async Task<string> BeginAsync(Guid user,string actor,MfaRequest request,CancellationToken ct)
    {
        var account=await ReauthenticateAsync(actor,request.Password,ct);if(account.Id!=user)throw new PortalException("Sesión no válida.",401);
        byte[] secret=RandomNumberGenerator.GetBytes(20);string encoded=Convert.ToBase64String(secret);
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await FenceAsync(c,t,account,ct);
        await using(var cmd=new NpgsqlCommand("""
            INSERT INTO atlas_admin_mfa(user_id,pending_secret,pending_until_utc) VALUES($1,$2,$3)
            ON CONFLICT(user_id) DO UPDATE SET pending_secret=EXCLUDED.pending_secret,pending_until_utc=EXCLUDED.pending_until_utc WHERE NOT atlas_admin_mfa.enabled
            """,c,t))
        {cmd.Parameters.AddWithValue(user);cmd.Parameters.AddWithValue(protector.Protect(encoded));cmd.Parameters.AddWithValue(clock.GetUtcNow().AddMinutes(10));if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new PortalException("El segundo factor ya está habilitado.",409);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.mfa.enrollment.started","AdminUser",user.ToString(),new{expiresInMinutes=10},ct);await t.CommitAsync(ct);return Base32(secret);
    }
    private long Match(string code,byte[] secret,long lastStep)
    {
        long current=clock.GetUtcNow().ToUnixTimeSeconds()/30;
        if(code.Length!=6 || code.Any(c=>!char.IsAsciiDigit(c)))return -1;
        foreach(long step in new[]{current,current-1,current+1})if(step>lastStep&&Equal(Totp(secret,step),code))return step;return -1;
    }
    public async Task ConfirmAsync(Guid user,Guid session,string actor,MfaRequest request,CancellationToken ct)
    {
        var account=await ReauthenticateAsync(actor,request.Password,ct);if(account.Id!=user)throw new PortalException("Sesión no válida.",401);
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);await FenceAsync(c,t,account,ct);
        bool enabled;
        await using(var query=new NpgsqlCommand("SELECT enabled FROM atlas_admin_mfa WHERE user_id=$1",c,t)){query.Parameters.AddWithValue(user);enabled=await query.ExecuteScalarAsync(ct) is true;}
        if(enabled&&!await VerifyLockedAsync(c,t,user,(request.Code??"").Trim().ToUpperInvariant(),ct)){await t.RollbackAsync(ct);await FailAsync(account,ct);throw new PortalException("Código incorrecto, expirado o utilizado.");}
        await using(var cmd=new NpgsqlCommand("UPDATE atlas_admin_sessions SET confirmed_at_utc=$1 WHERE id=$2 AND user_id=$3 AND revoked_at_utc IS NULL AND expires_at_utc>$1 AND security_revision=$4",c,t))
        {cmd.Parameters.AddWithValue(clock.GetUtcNow());cmd.Parameters.AddWithValue(session);cmd.Parameters.AddWithValue(user);cmd.Parameters.AddWithValue(account.SecurityRevision);if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new PortalException("Sesión no válida.",401);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.account.identity.confirmed","AdminUser",user.ToString(),new{secondFactor=enabled},ct);await t.CommitAsync(ct);
    }
    public async Task<string[]> EnableAsync(Guid user,string actor,MfaRequest request,CancellationToken ct,Guid? currentSession=null)
    {
        var account=await ReauthenticateAsync(actor,request.Password,ct);if(account.Id!=user)throw new PortalException("Sesión no válida.",401);
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await FenceAsync(c,t,account,ct);
        string protectedSecret;
        await using(var cmd=new NpgsqlCommand("SELECT pending_secret FROM atlas_admin_mfa WHERE user_id=$1 AND NOT enabled AND pending_until_utc>$2 FOR UPDATE",c,t))
        {cmd.Parameters.AddWithValue(user);cmd.Parameters.AddWithValue(clock.GetUtcNow());protectedSecret=await cmd.ExecuteScalarAsync(ct) as string??throw new PortalException("La preparación expiró. Inicie nuevamente.",409);}
        long step=Match(request.Code??"",Convert.FromBase64String(protector.Unprotect(protectedSecret)),-1);
        if(step<0){await t.RollbackAsync(ct);await FailAsync(account,ct);throw new PortalException("Código incorrecto o expirado.");}
        string[] codes=Enumerable.Range(0,10).Select(_=>Convert.ToHexString(RandomNumberGenerator.GetBytes(12))).ToArray();
        await using(var cmd=new NpgsqlCommand("""
            WITH changed AS (UPDATE atlas_admin_mfa SET enabled=true,protected_secret=$1,pending_secret=NULL,pending_until_utc=NULL,last_step=$2,recovery_hashes=$3,updated_at_utc=CURRENT_TIMESTAMP WHERE user_id=$4 RETURNING user_id),
            secured AS (UPDATE admin_users SET security_revision=security_revision+1 WHERE id=$4 RETURNING id)
            UPDATE atlas_admin_sessions SET revoked_at_utc=CASE WHEN id=$5::uuid THEN revoked_at_utc ELSE CURRENT_TIMESTAMP END,
            security_revision=CASE WHEN id=$5::uuid THEN security_revision+1 ELSE security_revision END WHERE user_id=$4 AND revoked_at_utc IS NULL
            """,c,t))
        {cmd.Parameters.AddWithValue(protectedSecret);cmd.Parameters.AddWithValue(step);cmd.Parameters.AddWithValue(codes.Select(x=>ReportFormats.Hash(Encoding.UTF8.GetBytes(x))).ToArray());cmd.Parameters.AddWithValue(user);cmd.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Uuid,Value=(object?)currentSession??DBNull.Value});await cmd.ExecuteNonQueryAsync(ct);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.mfa.enabled","AdminUser",user.ToString(),new{recoveryCodes=10,sessionsRevoked=true},ct);await t.CommitAsync(ct);return codes;
    }
    public async Task<bool> VerifyLoginAsync(AdminUserAccount account,string? code,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(code)||code.Length>40)return false;
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        bool valid=await VerifyLockedAsync(c,t,account.Id,code.Trim().ToUpperInvariant(),ct);
        if(!valid){await t.RollbackAsync(ct);await FailAsync(account,ct);return false;}
        await t.CommitAsync(ct);return true;
    }
    private async Task<bool> VerifyLockedAsync(NpgsqlConnection c,NpgsqlTransaction t,Guid user,string code,CancellationToken ct)
    {
        string secret;long last;string[] recovery;
        await using(var cmd=new NpgsqlCommand("SELECT protected_secret,last_step,recovery_hashes FROM atlas_admin_mfa WHERE user_id=$1 AND enabled FOR UPDATE",c,t))
        {cmd.Parameters.AddWithValue(user);await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return false;secret=r.GetString(0);last=r.GetInt64(1);recovery=r.GetFieldValue<string[]>(2);}
        long step=Match(code,Convert.FromBase64String(protector.Unprotect(secret)),last);string hash=ReportFormats.Hash(Encoding.UTF8.GetBytes(code));
        bool recovered=code.Length==24&&recovery.Any(x=>Equal(x,hash));if(step<0&&!recovered)return false;
        await using(var cmd=new NpgsqlCommand("UPDATE atlas_admin_mfa SET last_step=$1,recovery_hashes=$2 WHERE user_id=$3",c,t))
        {cmd.Parameters.AddWithValue(step>=0?step:last);cmd.Parameters.AddWithValue(recovered?recovery.Where(x=>x!=hash).ToArray():recovery);cmd.Parameters.AddWithValue(user);await cmd.ExecuteNonQueryAsync(ct);}
        if(recovered)await ConfigurationService.AuditAsync(c,t,user.ToString(),"admin.mfa.recovery.used","AdminUser",user.ToString(),new{remaining=recovery.Length-1},ct);
        return true;
    }
    public async Task DisableAsync(Guid user,string actor,MfaRequest request,CancellationToken ct)
    {
        var account=await ReauthenticateAsync(actor,request.Password,ct);if(account.Id!=user)throw new PortalException("Sesión no válida.",401);
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await FenceAsync(c,t,account,ct);
        if(!await VerifyLockedAsync(c,t,user,(request.Code??"").Trim().ToUpperInvariant(),ct)){await t.RollbackAsync(ct);await FailAsync(account,ct);throw new PortalException("Código incorrecto, expirado o utilizado.");}
        await using(var cmd=new NpgsqlCommand("""
            WITH changed AS (UPDATE atlas_admin_mfa SET enabled=false,protected_secret=NULL,pending_secret=NULL,recovery_hashes='{}',last_step=-1 WHERE user_id=$1 RETURNING user_id),
            secured AS (UPDATE admin_users SET security_revision=security_revision+1 WHERE id=$1 RETURNING id)
            UPDATE atlas_admin_sessions SET revoked_at_utc=CURRENT_TIMESTAMP WHERE user_id=$1 AND revoked_at_utc IS NULL
            """,c,t)){cmd.Parameters.AddWithValue(user);await cmd.ExecuteNonQueryAsync(ct);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.mfa.disabled","AdminUser",user.ToString(),new{sessionsRevoked=true},ct);await t.CommitAsync(ct);
    }
}
