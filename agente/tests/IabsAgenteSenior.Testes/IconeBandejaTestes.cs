using IabsAgenteSenior.Configuracao;
using IabsAgenteSenior.Monitor;
using IabsAgenteSenior.Servico;

namespace IabsAgenteSenior.Testes;

public sealed class IconeBandejaTestes : IDisposable
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(-3));
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), $"iabs-status-teste-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
    }

    private static StatusAgente Status(bool conectado = true, bool configurado = true, int minutosAtras = 0, int foraHaMinutos = 0, string? mensagem = null) => new(
        "2.0.0", Agora.AddMinutes(-minutosAtras), configurado, conectado,
        conectado ? Agora.AddHours(-1) : null,
        conectado ? null : Agora.AddMinutes(-foraHaMinutos),
        Agora.AddMinutes(-3), 42, "Cliente HML", true, mensagem);

    [Fact]
    public void Verde_quando_conectado()
    {
        var a = CalculoAparencia.Calcular(Status(), "Running", Agora);
        Assert.Equal(Nivel.Ok, a.Nivel);
        Assert.Contains(a.Linhas, l => l.StartsWith("Conectado desde 26/11", StringComparison.Ordinal));
        Assert.Contains("Lançar atestados: liberado", a.Linhas);
        Assert.True(a.Dica.Length <= 127);
    }

    [Fact]
    public void Vermelho_quando_servico_parado_ou_nao_instalado()
    {
        Assert.Equal(Nivel.Erro, CalculoAparencia.Calcular(Status(), "Stopped", Agora).Nivel);
        Assert.Equal(Nivel.Erro, CalculoAparencia.Calcular(null, null, Agora).Nivel);
    }

    [Fact]
    public void Queda_curta_e_amarela_e_longa_e_vermelha_com_a_mensagem()
    {
        Assert.Equal(Nivel.Atencao, CalculoAparencia.Calcular(Status(conectado: false, foraHaMinutos: 1), "Running", Agora).Nivel);
        var a = CalculoAparencia.Calcular(Status(conectado: false, foraHaMinutos: 10, mensagem: "A IABS recusou a chave do integrador."), "Running", Agora);
        Assert.Equal(Nivel.Erro, a.Nivel);
        Assert.Equal("A IABS recusou a chave do integrador.", a.Linhas[0]);
    }

    [Theory]
    [InlineData(false, 0)]   // aguardando configuração
    [InlineData(true, 30)]   // serviço sem dar notícias
    public void Amarelo(bool configurado, int minutosAtras) =>
        Assert.Equal(Nivel.Atencao, CalculoAparencia.Calcular(Status(configurado: configurado, minutosAtras: minutosAtras), "Running", Agora).Nivel);

    [Fact]
    public void Status_gravado_e_lido_de_volta()
    {
        var caminhos = Caminhos.Resolver(_pasta);
        var status = Status();
        StatusPainel.Gravar(caminhos, status);
        Assert.Equal(status, StatusPainel.Ler(caminhos));
    }

    [Fact]
    public void Sem_arquivo_de_status_le_nulo() =>
        Assert.Null(StatusPainel.Ler(Caminhos.Resolver(_pasta)));
}
