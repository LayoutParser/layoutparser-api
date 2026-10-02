using LayoutParserApi.Services.Database;
using Xunit;

namespace LayoutParserApi.Tests.Database
{
    /// <summary>Guarda de regressão da corrida de migração (#635): o applock precisa vir antes de qualquer DDL.</summary>
    public class GeneratedMapperArtifactSchemaDdlTests
    {
        [Fact]
        public void SchemaDdl_AdquireApplockAntesDeCriarOuAlterarColunas()
        {
            var ddl = SqlGeneratedMapperArtifactStore.SchemaDdl;
            var lockPos = ddl.IndexOf("sp_getapplock", StringComparison.Ordinal);
            Assert.True(lockPos >= 0);
            Assert.True(lockPos < ddl.IndexOf("CREATE TABLE", StringComparison.Ordinal));
            Assert.True(lockPos < ddl.IndexOf("ADD ProjectId", StringComparison.Ordinal));
            Assert.True(lockPos < ddl.IndexOf("ADD ProjectKey", StringComparison.Ordinal));
        }

        [Fact]
        public void SchemaDdl_FalhaSemLockAntesDeQualquerAlteracao()
        {
            var ddl = SqlGeneratedMapperArtifactStore.SchemaDdl;
            Assert.Contains("EXEC @rc = sp_getapplock", ddl, StringComparison.Ordinal);
            var guard = ddl.IndexOf("IF @rc < 0 THROW 50000", StringComparison.Ordinal);
            Assert.True(guard > ddl.IndexOf("sp_getapplock", StringComparison.Ordinal));
            Assert.True(guard < ddl.IndexOf("CREATE TABLE", StringComparison.Ordinal));
        }
    }
}
