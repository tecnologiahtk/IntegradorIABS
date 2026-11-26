using IabsAgenteSenior.Servico;

namespace IabsAgenteSenior.Monitor;

public enum Nivel
{
    Ok,
    Atencao,
    Erro,
}

/// <summary>O que o ícone mostra: cor, título e as linhas do menu.</summary>
public sealed record Aparencia(Nivel Nivel, string Titulo, IReadOnlyList<string> Linhas)
{
    /// <summary>Texto ao passar o mouse (o Windows corta em 127 caracteres).</summary>
    public string Dica
    {
        get
        {
            var texto = $"Integrador IABS: {Titulo}" + (Linhas.Count > 0 ? "\n" + string.Join("\n", Linhas.Take(2)) : "");
            return texto.Length <= 127 ? texto : texto[..124] + "...";
        }
    }
}

/// <summary>
/// Decide a aparência do ícone pelo estado do serviço do Windows e pelo
/// status.json que o serviço grava a cada 15 segundos.
/// </summary>
public static class CalculoAparencia
{
    public static readonly TimeSpan LimiteSemNoticias = TimeSpan.FromMinutes(2);

    /// <summary>Queda mais curta que isso é reconexão normal: amarelo, não vermelho.</summary>
    public static readonly TimeSpan ToleranciaReconexao = TimeSpan.FromMinutes(2);

    /// <param name="estadoServico">"Running", "Stopped"... ou nulo se o serviço não está instalado.</param>
    public static Aparencia Calcular(StatusAgente? s, string? estadoServico, DateTimeOffset agora)
    {
        if (estadoServico is null) return new(Nivel.Erro, "serviço não instalado", ["Reinstale o Integrador IABS."]);
        if (estadoServico != "Running")
        {
            return new(Nivel.Erro, "serviço parado", ["A IABS não consegue falar com o Senior.", "Abra a configuração e clique em Iniciar serviço."]);
        }
        if (s is null) return new(Nivel.Atencao, "em execução, sem status ainda", ["O serviço grava o status a cada 15 segundos."]);
        if (!s.Configurado) return new(Nivel.Atencao, "aguardando configuração", ["Abra a configuração e preencha as abas 1 e 2."]);

        var linhas = new List<string>();
        if (s.Conectado && s.ConectadoDesde is { } desde) linhas.Add($"Conectado desde {desde.LocalDateTime:dd/MM HH:mm}");
        if (s.ConexaoIabs is not null) linhas.Add($"Conexão na IABS: {s.ConexaoIabs}");
        if (s.UltimoPedidoEm is { } pedido) linhas.Add($"Último pedido: {pedido.LocalDateTime:dd/MM HH:mm}");
        linhas.Add(s.GravacaoLiberada ? "Lançar atestados: liberado" : "Lançar atestados: desligado");

        var semNoticias = agora - s.AtualizadoEm;
        if (semNoticias > LimiteSemNoticias)
        {
            return new(Nivel.Atencao, $"serviço sem dar notícias há {(int)semNoticias.TotalMinutes} min", linhas);
        }
        if (!s.Conectado)
        {
            if (s.Mensagem is not null) linhas.Insert(0, s.Mensagem);
            var fora = s.DesconectadoDesde is { } d ? agora - d : TimeSpan.Zero;
            return fora <= ToleranciaReconexao
                ? new(Nivel.Atencao, "conectando à IABS", linhas)
                : new(Nivel.Erro, $"sem conexão com a IABS há {(int)fora.TotalMinutes} min", linhas);
        }
        return new(Nivel.Ok, "conectado à IABS", linhas);
    }
}
