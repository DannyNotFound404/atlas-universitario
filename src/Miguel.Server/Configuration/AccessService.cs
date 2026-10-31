using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Miguel.Server.Admin;
using Miguel.Server.Reports;
using Npgsql;

namespace Miguel.Server.Configuration;

public sealed record AccountCreate(string Username,string DisplayName,string Role,string Password);
public sealed record AccountUpdate(int Revision,string DisplayName,string Role,bool Enabled);
public sealed record PasswordReset(int Revision,string Password);
public sealed class AccessService(NpgsqlDataSource db,IPasswordHasher<AdminUserAccount> hasher,TimeProvider clock)
{
    public async Task RequireRecentAsync(Guid user,Guid session,CancellationToken ct)
    {
        await using var cmd=db.CreateCommand("SELECT id FROM atlas_admin_sessions WHERE id=$1 AND user_id=$2 AND revoked_at_utc IS NULL AND expires_at_utc>$3 AND confirmed_at_utc>$4");
        cmd.Parameters.AddWithValue(session);cmd.Parameters.AddWithValue(user);cmd.Parameters.AddWithValue(clock.GetUtcNow());cmd.Parameters.AddWithValue(clock.GetUtcNow().AddMinutes(-5));
        if(await cmd.ExecuteScalarAsync(ct) is not Guid)throw new PortalException("Confirme su identidad para administrar las cuentas.",428);
    }
    public async Task<Guid> CreateSessionAsync(Guid user,CancellationToken ct,string? expectedPasswordHash=null,bool? mfaChecked=null,int? expectedRevision=null)
    {
        Guid id=Guid.NewGuid();await using var cmd=db.CreateCommand("""
            INSERT INTO atlas_admin_sessions(id,user_id,security_revision,created_at_utc,expires_at_utc)
            SELECT $1,id,security_revision,$3,$4 FROM admin_users WHERE id=$2 AND enabled
            AND ($5::text IS NULL OR password_hash=$5)
            AND ($6::boolean IS NULL OR COALESCE((SELECT enabled FROM atlas_admin_mfa WHERE user_id=$2),false)=$6)
            AND ($7::integer IS NULL OR security_revision=$7) RETURNING id
            """);cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(user);cmd.Parameters.AddWithValue(clock.GetUtcNow());cmd.Parameters.AddWithValue(clock.GetUtcNow().AddHours(1));
        cmd.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Text,Value=(object?)expectedPasswordHash??DBNull.Value});
        cmd.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Boolean,Value=(object?)mfaChecked??DBNull.Value});
        cmd.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Integer,Value=(object?)expectedRevision??DBNull.Value});
        if(await cmd.ExecuteScalarAsync(ct) is not Guid)throw new PortalException("La cuenta ya no está disponible.",401);return id;
    }
    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        bool valid=false;
        if(Guid.TryParse(context.Principal?.FindFirstValue("atlas-session"),out var session) && Guid.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier),out var user))
        {
            await using var cmd=db.CreateCommand("""
                SELECT u.role,u.username,u.display_name FROM atlas_admin_sessions s JOIN admin_users u ON u.id=s.user_id
                WHERE s.id=$1 AND u.id=$2 AND u.enabled AND s.revoked_at_utc IS NULL AND s.expires_at_utc>$3 AND s.security_revision=u.security_revision
                """);cmd.Parameters.AddWithValue(session);cmd.Parameters.AddWithValue(user);cmd.Parameters.AddWithValue(clock.GetUtcNow());
            await using var r=await cmd.ExecuteReaderAsync(context.HttpContext.RequestAborted);
            if(await r.ReadAsync(context.HttpContext.RequestAborted))valid=r.GetString(0)==context.Principal?.FindFirstValue(ClaimTypes.Role) && r.GetString(1)==context.Principal?.Identity?.Name;
        }
        if(!valid){context.RejectPrincipal();await context.HttpContext.SignOutAsync(AdminEndpointExtensions.AuthenticationScheme);}
    }
    public async Task RevokeAsync(Guid user,Guid? session,string actor,CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await using(var cmd=new NpgsqlCommand("UPDATE atlas_admin_sessions SET revoked_at_utc=CURRENT_TIMESTAMP WHERE user_id=$1 AND ($2::uuid IS NULL OR id=$2) AND revoked_at_utc IS NULL",c,t))
        {cmd.Parameters.AddWithValue(user);cmd.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Uuid,Value=(object?)session??DBNull.Value});await cmd.ExecuteNonQueryAsync(ct);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.account.sessions.revoked","AdminUser",user.ToString(),new{all=session is null},ct);await t.CommitAsync(ct);
    }
    public static void Password(string? password,string username)
    {
        if(password is null || password.Length is <14 or >128 || password.Contains(username,StringComparison.OrdinalIgnoreCase))throw new PortalException("Use una contraseña de 14 a 128 caracteres que no incluya el usuario.");
    }
    public async Task<Guid> CreateAsync(AccountCreate request,string actor,CancellationToken ct)
    {
        string normalized=AdminAccountService.NormalizeUsername(request.Username);Password(request.Password,normalized);
        string name=ConfigurationService.Text(request.DisplayName,128,"el nombre");if(!AdminRoles.IsValid(request.Role))throw new PortalException("Rol no válido.");
        var account=new AdminUserAccount(Guid.NewGuid(),request.Username.Trim(),normalized,name,"",request.Role,true,0,null,clock.GetUtcNow(),null);
        string hash=hasher.HashPassword(account,request.Password);
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await using(var cmd=new NpgsqlCommand("INSERT INTO admin_users(id,username,normalized_username,display_name,password_hash,role,enabled,created_at_utc) VALUES($1,$2,$3,$4,$5,$6,true,$7)",c,t))
        {cmd.Parameters.AddWithValue(account.Id);cmd.Parameters.AddWithValue(account.Username);cmd.Parameters.AddWithValue(normalized);cmd.Parameters.AddWithValue(name);cmd.Parameters.AddWithValue(hash);cmd.Parameters.AddWithValue(account.Role);cmd.Parameters.AddWithValue(account.CreatedAtUtc);await cmd.ExecuteNonQueryAsync(ct);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.account.created","AdminUser",account.Id.ToString(),new{account.Username,account.DisplayName,account.Role},ct);await t.CommitAsync(ct);return account.Id;
    }
    public async Task UpdateAsync(Guid user,AccountUpdate request,string actor,CancellationToken ct)
    {
        string name=ConfigurationService.Text(request.DisplayName,128,"el nombre");if(!AdminRoles.IsValid(request.Role))throw new PortalException("Rol no válido.");
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        // Serialize every role/enable change so concurrent demotions cannot remove the last administrator.
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(393204)",c,t))await gate.ExecuteNonQueryAsync(ct);
        string beforeRole,beforeName;bool beforeEnabled;int revision;
        await using(var cmd=new NpgsqlCommand("SELECT role,display_name,enabled,security_revision FROM admin_users WHERE id=$1 FOR UPDATE",c,t))
        {cmd.Parameters.AddWithValue(user);await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new PortalException("Cuenta no encontrada.",404);beforeRole=r.GetString(0);beforeName=r.GetString(1);beforeEnabled=r.GetBoolean(2);revision=r.GetInt32(3);}
        if(revision!=request.Revision)throw new PortalException("La cuenta cambió. Actualice la pantalla.",409);
        if(beforeRole==AdminRoles.Administrator && beforeEnabled && (!request.Enabled || request.Role!=AdminRoles.Administrator))
        {await using var cmd=new NpgsqlCommand("SELECT COUNT(*) FROM admin_users WHERE enabled AND role='Administrator' AND id<>$1",c,t);cmd.Parameters.AddWithValue(user);if((long)(await cmd.ExecuteScalarAsync(ct))!<1)throw new PortalException("Debe quedar al menos un administrador habilitado.",409);}
        await using(var cmd=new NpgsqlCommand("""
            WITH changed AS (UPDATE admin_users SET display_name=$1,role=$2,enabled=$3,security_revision=security_revision+1 WHERE id=$4 RETURNING id),
            revoked AS (UPDATE atlas_admin_sessions SET revoked_at_utc=CURRENT_TIMESTAMP WHERE user_id=$4 AND revoked_at_utc IS NULL RETURNING id)
            UPDATE atlas_report_schedules SET enabled=false,suspension_reason='OWNER_NOT_AUTHORIZED',revision=revision+1
            WHERE owner_id=$4 AND enabled AND (NOT $3 OR $2 NOT IN ('Administrator','Auditor','TechnologyManager'))
            """,c,t))
        {cmd.Parameters.AddWithValue(name);cmd.Parameters.AddWithValue(request.Role);cmd.Parameters.AddWithValue(request.Enabled);cmd.Parameters.AddWithValue(user);await cmd.ExecuteNonQueryAsync(ct);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.account.updated","AdminUser",user.ToString(),new{before=new{displayName=beforeName,role=beforeRole,enabled=beforeEnabled},after=new{displayName=name,role=request.Role,enabled=request.Enabled},sessionsRevoked=true},ct);await t.CommitAsync(ct);
    }
    public async Task ResetAsync(Guid user,PasswordReset request,string actor,CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        string username;int revision;
        await using(var cmd=new NpgsqlCommand("SELECT username,security_revision FROM admin_users WHERE id=$1 FOR UPDATE",c,t))
        {cmd.Parameters.AddWithValue(user);await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new PortalException("Cuenta no encontrada.",404);username=r.GetString(0);revision=r.GetInt32(1);}
        if(request.Revision!=revision)throw new PortalException("La cuenta cambió. Actualice la pantalla.",409);Password(request.Password,username);
        string hash=hasher.HashPassword(new(user,username,username.ToUpperInvariant(),username,"","Auditor",true,0,null,clock.GetUtcNow(),null),request.Password);
        await using(var cmd=new NpgsqlCommand("""
            WITH changed AS (UPDATE admin_users SET password_hash=$1,security_revision=security_revision+1,failed_attempts=0,locked_until_utc=NULL WHERE id=$2 RETURNING id)
            UPDATE atlas_admin_sessions SET revoked_at_utc=CURRENT_TIMESTAMP WHERE user_id=$2 AND revoked_at_utc IS NULL
            """,c,t))
        {cmd.Parameters.AddWithValue(hash);cmd.Parameters.AddWithValue(user);await cmd.ExecuteNonQueryAsync(ct);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.account.password.reset","AdminUser",user.ToString(),new{sessionsRevoked=true},ct);await t.CommitAsync(ct);
    }
}
