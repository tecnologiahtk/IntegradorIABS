using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace IabsAgenteSenior.Diagnostico;

/// <summary>
/// Log em arquivo, um por dia, guardado por 30 dias, em logs\ na pasta de dados.
/// Sem biblioteca externa (menos DLL para o antivírus avaliar). Tudo que parecer
/// CPF ou e-mail é mascarado antes de gravar, mesmo que escape de algum ponto
/// do código.
/// </summary>
public sealed partial class ArquivoLoggerProvider : ILoggerProvider
{
    private readonly string _pasta;
    private readonly LogLevel _nivelMinimo;
    private readonly Lock _trava = new();

    public ArquivoLoggerProvider(string pasta, LogLevel nivelMinimo = LogLevel.Information)
    {
        _pasta = pasta;
        _nivelMinimo = nivelMinimo;
        try
        {
            Directory.CreateDirectory(pasta);
            foreach (var antigo in Directory.GetFiles(pasta, "agente-*.log"))
            {
                if (File.GetLastWriteTimeUtc(antigo) < DateTime.UtcNow.AddDays(-30)) File.Delete(antigo);
            }
        }
        catch (IOException)
        {
            // log não pode derrubar o agente
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public ILogger CreateLogger(string categoryName) => new ArquivoLogger(this, categoryName);

    [GeneratedRegex(@"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b")]
    private static partial Regex PadraoCpf();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex PadraoEmail();

    public static string Mascarar(string texto) =>
        PadraoEmail().Replace(PadraoCpf().Replace(texto, "***cpf***"), "***email***");

    private void Gravar(LogLevel nivel, string categoria, string mensagem, Exception? ex)
    {
        var linha = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(nivel.ToString().ToUpperInvariant()[..4]).Append("] ")
            .Append(categoria[(categoria.LastIndexOf('.') + 1)..]).Append(": ")
            .Append(mensagem);
        if (ex is not null) linha.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message.Split('\n')[0]);
        var texto = Mascarar(linha.ToString()) + Environment.NewLine;
        lock (_trava)
        {
            try
            {
                File.AppendAllText(Path.Combine(_pasta, $"agente-{DateTime.Now:yyyyMMdd}.log"), texto, Encoding.UTF8);
            }
            catch (IOException)
            {
                // antivírus lendo o arquivo: perde a linha, não o agente
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
    }

    private sealed class ArquivoLogger(ArquivoLoggerProvider dono, string categoria) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= dono._nivelMinimo && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            dono.Gravar(logLevel, categoria, formatter(state, exception), exception);
        }
    }
}
