using LayoutParserApi.Services.Transformation.Ai;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Services.Transformation.Ai
{
    /// <summary>Issue #642 — gatilho de criação do mapeador ausente: idempotência por layout e resiliência.</summary>
    public sealed class MissingMapperGenerationTriggerTests
    {
        private sealed class CountingScopeFactory : IServiceScopeFactory
        {
            public int Calls;
            public IServiceScope CreateScope()
            {
                Interlocked.Increment(ref Calls);
                throw new InvalidOperationException("SQL fora do ar (simulado)");
            }
        }

        private static async Task WaitForAsync(Func<bool> cond)
        {
            for (var i = 0; i < 100 && !cond(); i++) await Task.Delay(20);
        }

        [Fact]
        public async Task MesmoLayoutDentroDoCooldown_DisparaUmaVez_ENuncaLanca()
        {
            var factory = new CountingScopeFactory();
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var trigger = new MissingMapperGenerationTrigger(factory, NullLogger<MissingMapperGenerationTrigger>.Instance, () => now);

            trigger.TriggerForLayout("LAY_X", "map_not_found");
            trigger.TriggerForLayout("lay_x", "xsl_not_found"); // case-insensitive, mesma janela
            await WaitForAsync(() => factory.Calls >= 1);
            await Task.Delay(100);

            Assert.Equal(1, factory.Calls);
        }

        [Fact]
        public async Task AposCooldown_DisparaDeNovo()
        {
            var factory = new CountingScopeFactory();
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var trigger = new MissingMapperGenerationTrigger(factory, NullLogger<MissingMapperGenerationTrigger>.Instance, () => now);

            trigger.TriggerForLayout("LAY_X", "map_not_found");
            now += MissingMapperGenerationTrigger.Cooldown + TimeSpan.FromSeconds(1);
            trigger.TriggerForLayout("LAY_X", "map_not_found");
            await WaitForAsync(() => factory.Calls >= 2);

            Assert.Equal(2, factory.Calls);
        }

        [Fact]
        public void LayoutVazio_Ignora()
        {
            var factory = new CountingScopeFactory();
            var trigger = new MissingMapperGenerationTrigger(factory, NullLogger<MissingMapperGenerationTrigger>.Instance);
            trigger.TriggerForLayout("  ", "map_not_found");
            trigger.TriggerForLayout(null, "map_not_found");
            Assert.Equal(0, factory.Calls);
        }
    }
}
