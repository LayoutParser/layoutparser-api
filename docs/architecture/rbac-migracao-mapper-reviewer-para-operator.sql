-- RBAC 4 papeis (2026-10-01): migra papeis legados mapper/reviewer -> operator.
-- EXECUTAR SOMENTE na VM identity (elson@172.25.32.5, container layoutparser-identity-sql,
-- banco LayoutParserIdentity), por @lp-devops. NUNCA em 172.31.249.51 (somente leitura).
-- Idempotente: reexecutar nao altera nada. O codigo ja trata legado como operator,
-- entao a ordem em relacao ao deploy e indiferente.

SET XACT_ABORT ON;

-- 1) Contagem antes (somente leitura)
SELECT Role, COUNT(*) AS Total FROM dbo.tbLpWorkspaceMembership GROUP BY Role ORDER BY Total DESC;

-- 2) Backup dos ids afetados (rollback: UPDATE de volta usando esta tabela)
IF OBJECT_ID('dbo.tbLpWorkspaceMembership_RoleBackup_20261001') IS NULL
BEGIN
    SELECT WorkspaceMembershipId, Role AS RoleAnterior
    INTO dbo.tbLpWorkspaceMembership_RoleBackup_20261001
    FROM dbo.tbLpWorkspaceMembership
    WHERE Role IN ('mapper', 'reviewer');
END

-- 3) Migracao
BEGIN TRAN;
UPDATE dbo.tbLpWorkspaceMembership SET Role = 'operator' WHERE Role IN ('mapper', 'reviewer');
SELECT @@ROWCOUNT AS LinhasMigradas;
COMMIT;

-- 4) Verificacao: deve retornar 0
SELECT COUNT(*) AS LegadosRestantes FROM dbo.tbLpWorkspaceMembership WHERE Role IN ('mapper', 'reviewer');
