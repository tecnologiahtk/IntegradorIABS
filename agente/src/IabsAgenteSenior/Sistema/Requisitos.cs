using System.Runtime.InteropServices;
using System.Security.Principal;

namespace IabsAgenteSenior.Sistema;

/// <summary>
/// Requisitos da máquina. O instalador confere os mesmos itens antes de
/// instalar (condições do MSI); aqui eles aparecem no "configurar" e no comando
/// "requisitos". Não há runtime a instalar: o .NET vai dentro da pasta do agente.
/// </summary>
public static class Requisitos
{
    public const int BuildMinimo = 17763; // Windows Server 2019 / Windows 10 1809
    public const long EspacoMinimoBytes = 2L * 1024 * 1024 * 1024;

    public sealed record Item(string Nome, bool Ok, string Detalhe);

    public static List<Item> Verificar(string pastaDados)
    {
        var lista = new List<Item>();
        var build = Environment.OSVersion.Version.Build;
        lista.Add(new("Windows 64 bits", Environment.Is64BitOperatingSystem, RuntimeInformation.OSArchitecture.ToString()));
        lista.Add(new("Windows Server 2019 ou mais novo (build 17763+)", OperatingSystem.IsWindows() && build >= BuildMinimo,
            $"{RuntimeInformation.OSDescription} (build {build})"));

        var raiz = Path.GetPathRoot(Path.GetFullPath(pastaDados)) ?? "C:\\";
        var livre = new DriveInfo(raiz).AvailableFreeSpace;
        lista.Add(new("2 GB livres no disco da pasta de dados", livre >= EspacoMinimoBytes, $"{livre / 1024 / 1024 / 1024} GB livres em {raiz}"));

        lista.Add(new("Executando como administrador (necessário para configurar)", EhAdministrador(), EhAdministrador() ? "sim" : "não"));
        return lista;
    }

    public static bool EhAdministrador()
    {
        using var identidade = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identidade).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
