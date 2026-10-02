using LayoutParserApi.Services.Email;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>Outbox de e-mail no <c>IdentityDatabase:*</c> (nunca no SQL compartilhado do Sysmiddle).</summary>
    public sealed class SqlEmailOutboxStore : IEmailOutboxStore
    {
        private readonly string _connectionString;

        public SqlEmailOutboxStore(IConfiguration configuration)
        {
            _connectionString =
                $"Server={configuration["IdentityDatabase:Server"]};Database={configuration["IdentityDatabase:Database"]};" +
                $"User Id={configuration["IdentityDatabase:UserId"]};Password={configuration["IdentityDatabase:Password"]};TrustServerCertificate=True;";
        }

        private async Task<SqlConnection> OpenAsync(CancellationToken ct)
        {
            var c = new SqlConnection(_connectionString);
            await c.OpenAsync(ct);
            await SqlIdentityWorkspaceStore.EnsureSchemaAsync(c, ct);
            return c;
        }

        public async Task<EnqueueResult> EnqueueAsync(string toEmail, string template, string dedupeKey, string subject, string body, CancellationToken ct)
        {
            using var c = await OpenAsync(ct);
            using var cmd = new SqlCommand(
                @"DECLARE @Existing UNIQUEIDENTIFIER = (SELECT TOP (1) EmailId FROM dbo.tbLpEmailOutbox
                        WHERE ToEmail = @To AND Template = @Template AND DedupeKey = @Key
                          AND CreatedAt > DATEADD(HOUR, -24, SYSUTCDATETIME()) ORDER BY CreatedAt DESC);
                  IF @Existing IS NOT NULL
                    SELECT CAST(0 AS BIT), @Existing;
                  ELSE
                  BEGIN
                    DECLARE @New UNIQUEIDENTIFIER = NEWID();
                    INSERT INTO dbo.tbLpEmailOutbox (EmailId, ToEmail, Template, DedupeKey, Subject, Body, Status, Attempts, NextAttemptAt, CreatedAt)
                    VALUES (@New, @To, @Template, @Key, @Subject, @Body, 'pending', 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                    SELECT CAST(1 AS BIT), @New;
                  END", c);
            cmd.Parameters.AddWithValue("@To", toEmail);
            cmd.Parameters.AddWithValue("@Template", template);
            cmd.Parameters.AddWithValue("@Key", dedupeKey);
            cmd.Parameters.AddWithValue("@Subject", subject);
            cmd.Parameters.AddWithValue("@Body", body);
            using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return new EnqueueResult(r.GetBoolean(0), r.GetGuid(1));
        }

        public async Task<OutboxEmail?> ClaimNextAsync(int maxAttempts, CancellationToken ct)
        {
            using var c = await OpenAsync(ct);
            // Reivindica de forma atômica (READPAST evita duas instâncias pegarem o mesmo e-mail).
            using var cmd = new SqlCommand(
                @"WITH next AS (
                    SELECT TOP (1) * FROM dbo.tbLpEmailOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE Status = 'pending' AND NextAttemptAt <= SYSUTCDATETIME() AND Attempts < @Max
                    ORDER BY CreatedAt)
                  UPDATE next SET Status = 'sending', Attempts = Attempts + 1
                  OUTPUT inserted.EmailId, inserted.ToEmail, inserted.Subject, inserted.Body, inserted.Attempts, inserted.Template, inserted.DedupeKey;", c);
            cmd.Parameters.AddWithValue("@Max", maxAttempts);
            using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct)
                ? new OutboxEmail(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetString(6))
                : null;
        }

        public async Task MarkSentAsync(Guid emailId, CancellationToken ct)
        {
            using var c = await OpenAsync(ct);
            using var cmd = new SqlCommand("UPDATE dbo.tbLpEmailOutbox SET Status='sent', SentAt=SYSUTCDATETIME(), LastError=NULL WHERE EmailId=@Id;", c);
            cmd.Parameters.AddWithValue("@Id", emailId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task MarkFailedAsync(Guid emailId, string error, int maxAttempts, CancellationToken ct)
        {
            using var c = await OpenAsync(ct);
            // Backoff exponencial (2^tentativas minutos); esgotadas as tentativas, fica 'failed'.
            using var cmd = new SqlCommand(
                @"UPDATE dbo.tbLpEmailOutbox
                  SET Status = CASE WHEN Attempts >= @Max THEN 'failed' ELSE 'pending' END,
                      NextAttemptAt = DATEADD(MINUTE, POWER(2, Attempts), SYSUTCDATETIME()),
                      LastError = LEFT(@Error, 500)
                  WHERE EmailId = @Id;", c);
            cmd.Parameters.AddWithValue("@Id", emailId);
            cmd.Parameters.AddWithValue("@Max", maxAttempts);
            cmd.Parameters.AddWithValue("@Error", error);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<int> CountSentLast24hAsync(CancellationToken ct)
        {
            using var c = await OpenAsync(ct);
            using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.tbLpEmailOutbox WHERE Status='sent' AND SentAt > DATEADD(HOUR,-24,SYSUTCDATETIME());", c);
            return (int)(await cmd.ExecuteScalarAsync(ct))!;
        }

        // 'sending' é transitório: para o rastreio conta como pending.
        private const string StatusNormalizado = "CASE WHEN Status = 'sending' THEN 'pending' ELSE Status END";

        public async Task<IReadOnlyList<OutboxEmailStatus>> ListAsync(string dedupeKey, string? toEmail, int skip, int take, CancellationToken ct)
        {
            using var c = await OpenAsync(ct);
            using var cmd = new SqlCommand(
                $@"SELECT EmailId, ToEmail, Template, {StatusNormalizado}, Attempts, CreatedAt, NextAttemptAt, SentAt, LastError
                   FROM dbo.tbLpEmailOutbox
                   WHERE DedupeKey = @Key AND (@To IS NULL OR ToEmail = @To)
                   ORDER BY CreatedAt DESC
                   OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;", c);
            cmd.Parameters.AddWithValue("@Key", dedupeKey);
            cmd.Parameters.AddWithValue("@To", (object?)toEmail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Skip", skip);
            cmd.Parameters.AddWithValue("@Take", take);
            var list = new List<OutboxEmailStatus>();
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new OutboxEmailStatus(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                    r.GetDateTime(5), r.IsDBNull(6) ? null : r.GetDateTime(6), r.IsDBNull(7) ? null : r.GetDateTime(7),
                    r.IsDBNull(8) ? null : r.GetString(8)));
            return list;
        }

        public async Task<IReadOnlyDictionary<string, string>> GetLatestStatusByEmailAsync(string dedupeKey, CancellationToken ct)
        {
            using var c = await OpenAsync(ct);
            using var cmd = new SqlCommand(
                $@"SELECT ToEmail, {StatusNormalizado} FROM (
                     SELECT ToEmail, Status, ROW_NUMBER() OVER (PARTITION BY ToEmail ORDER BY CreatedAt DESC) rn
                     FROM dbo.tbLpEmailOutbox WHERE DedupeKey = @Key) t WHERE rn = 1;", c);
            cmd.Parameters.AddWithValue("@Key", dedupeKey);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                map[r.GetString(0)] = r.GetString(1);
            return map;
        }
    }
}
