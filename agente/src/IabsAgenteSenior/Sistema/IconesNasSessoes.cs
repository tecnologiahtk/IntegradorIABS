using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace IabsAgenteSenior.Sistema;

/// <summary>
/// Abre o ícone perto do relógio na sessão de cada usuário logado (2.1.4), no
/// fim da instalação e de toda atualização, inclusive a automática: o
/// instalador fecha o ícone para trocar os arquivos, e antes ele só voltava no
/// próximo login. Roda como SYSTEM (custom action do MSI, depois de ligar o
/// serviço): abre o IabsAgenteSenior.Monitor.exe da pasta do programa com o
/// token do próprio usuário (sem elevação), na área de trabalho dele. Sessão
/// que já tem o ícone fica como está. Nos logins seguintes, quem abre é a
/// entrada "IntegradorIABS" em HKLM\...\CurrentVersion\Run (todos os usuários).
/// </summary>
public static class IconesNasSessoes
{
    public static List<string> Abrir(string monitorExe)
    {
        var mensagens = new List<string>();
        if (!File.Exists(monitorExe))
        {
            mensagens.Add($"AVISO: ícone não encontrado em {monitorExe}.");
            return mensagens;
        }
        var comIcone = SessoesComIcone(Path.GetFileNameWithoutExtension(monitorExe));
        foreach (var sessao in SessoesDeUsuario())
        {
            if (comIcone.Contains(sessao)) continue;
            try
            {
                AbrirNaSessao(sessao, monitorExe);
                mensagens.Add($"Ícone aberto na sessão {sessao}.");
            }
            catch (Win32Exception ex)
            {
                mensagens.Add($"AVISO: não abri o ícone na sessão {sessao} ({ex.NativeErrorCode}).");
            }
        }
        if (mensagens.Count == 0) mensagens.Add("Nenhuma sessão sem o ícone.");
        return mensagens;
    }

    private static HashSet<int> SessoesComIcone(string nome)
    {
        var sessoes = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(nome))
        {
            try
            {
                sessoes.Add(p.SessionId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // processo saiu no meio
            }
            finally
            {
                p.Dispose();
            }
        }
        return sessoes;
    }

    /// <summary>Sessões com usuário (ativas ou desconectadas da área de trabalho remota), sem a sessão 0 dos serviços.</summary>
    private static List<int> SessoesDeUsuario()
    {
        var lista = new List<int>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var total)) return lista;
        try
        {
            var tamanho = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < total; i++)
            {
                var s = Marshal.PtrToStructure<WtsSessionInfo>(buffer + i * tamanho);
                if (s.SessionId != 0 && s.State is WtsActive or WtsDisconnected) lista.Add(s.SessionId);
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
        return lista;
    }

    private static void AbrirNaSessao(int sessao, string exe)
    {
        if (!WTSQueryUserToken(sessao, out var token)) throw new Win32Exception(); // sessão sem usuário logado
        try
        {
            if (!CreateEnvironmentBlock(out var ambiente, token, false)) ambiente = IntPtr.Zero;
            try
            {
                var inicio = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default" };
                var pasta = Path.GetDirectoryName(exe);
                // fora do job da instalação, se ele deixar: o ícone fica depois de o instalador terminar
                if (!CriarProcesso(token, exe, pasta, ambiente, ref inicio, CriarUnicode | ForaDoJob, out var processo)
                    && !CriarProcesso(token, exe, pasta, ambiente, ref inicio, CriarUnicode, out processo))
                {
                    throw new Win32Exception();
                }
                CloseHandle(processo.hProcess);
                CloseHandle(processo.hThread);
            }
            finally
            {
                if (ambiente != IntPtr.Zero) DestroyEnvironmentBlock(ambiente);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool CriarProcesso(IntPtr token, string exe, string? pasta, IntPtr ambiente, ref StartupInfo inicio, uint flags, out ProcessInformation processo) =>
        CreateProcessAsUser(token, exe, new StringBuilder($"\"{exe}\""), IntPtr.Zero, IntPtr.Zero, false, flags, ambiente, pasta, ref inicio, out processo);

    // ---- Win32 -------------------------------------------------------------------------------
    private const int WtsActive = 0;
    private const int WtsDisconnected = 4;
    private const uint CriarUnicode = 0x00000400; // CREATE_UNICODE_ENVIRONMENT
    private const uint ForaDoJob = 0x01000000;    // CREATE_BREAKAWAY_FROM_JOB

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr servidor, int reservado, int versao, out IntPtr sessoes, out int total);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memoria);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(int sessao, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr ambiente, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool herdar);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr ambiente);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(IntPtr token, string aplicacao, StringBuilder linha, IntPtr atributosProcesso, IntPtr atributosThread,
        [MarshalAs(UnmanagedType.Bool)] bool herdarHandles, uint flags, IntPtr ambiente, string? pasta, ref StartupInfo inicio, out ProcessInformation processo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
