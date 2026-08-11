using System.ComponentModel;
using System.Text.Json;
using SagaBackend.Services;
using ModelContextProtocol.Server;

namespace SagaBackend;

/// <summary>
/// Ferramentas MCP do Saga, publicadas em <c>/mcp</c>.
/// </summary>
/// <remarks>
/// Sob o Yggdrasil elas aparecem no endpoint agregado como <c>saga__*</c>, ao lado das
/// dos outros módulos — o prefixo é aplicado pelo hub, não aqui.
///
/// Corte pequeno de propósito: consultar a biblioteca, ver o que ficou pela metade, acompanhar
/// e disparar transcodificação. São as perguntas que se faz sobre um servidor de mídia sem
/// abrir a interface. Reproduzir vídeo não é tarefa de agente, e por isso não há ferramenta
/// para isso.
///
/// Nada aqui escreve na biblioteca: o Sága monta <c>Media</c> somente leitura de
/// propósito, e é o que impede um transcode acidental de tocar no acervo.
/// </remarks>
[McpServerToolType]
public sealed class SagaTools(
    MediaLibraryService library,
    WatchHistoryService history,
    HlsService hls,
    ServerSettings settings)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // camelCase em tudo: sem a política, os records saem em PascalCase e os objetos
        // anônimos saem como escritos, então a mesma resposta mistura `Path` e
        // `positionSeconds`. Ruído gratuito para quem consome.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // As descrições e mensagens são em português e vão para um agente ler; `ç` no
        // lugar de `ç` não ajuda ninguém.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "search_media")]
    [Description(
        "Procura títulos na biblioteca por parte do nome ou da pasta. " +
        "Devolve o caminho relativo, a URL de streaming e se o HLS já está pronto. " +
        "Sem termo, lista o começo da biblioteca.")]
    public string SearchMedia(
        [Description("Parte do nome ou do caminho, ex.: 'submarino' ou 'Series/Dark'. Opcional.")] string? query = null,
        [Description("Quantos resultados no máximo. Padrão 50.")] int limit = 50)
    {
        var matches = library.Search(query ?? "", Math.Clamp(limit, 1, 200));

        return JsonSerializer.Serialize(new
        {
            count = matches.Count,
            // Avisar que cortou importa: sem isso, "achei 50" e "há exatamente 50" ficam
            // indistinguíveis, e quem consome decide errado se refina a busca.
            truncated = matches.Count >= Math.Clamp(limit, 1, 200),
            matches,
        }, Json);
    }

    [McpServerTool(Name = "list_continue_watching")]
    [Description("Lista o que foi começado e não terminado, com a posição em segundos e a duração total.")]
    public async Task<string> ListContinueWatching(
        [Description("Quantos itens no máximo. Padrão 20.")] int limit = 20)
    {
        var entries = await history.GetContinueWatchingAsync(Math.Clamp(limit, 1, 100));

        return JsonSerializer.Serialize(entries.Select(e => new
        {
            e.Path,
            positionSeconds = Math.Round(e.PositionSeconds, 1),
            durationSeconds = Math.Round(e.DurationSeconds, 1),
            // O percentual é o que responde "vale a pena retomar?" sem obrigar a conta.
            percent = e.DurationSeconds > 0
                ? Math.Round(e.PositionSeconds / e.DurationSeconds * 100, 1)
                : 0,
            lastWatchedUtc = e.LastWatchedUtc,
        }), Json);
    }

    [McpServerTool(Name = "list_transcode_jobs")]
    [Description("Lista as transcodificações em andamento. Útil para saber por que o servidor está ocupado.")]
    public string ListTranscodeJobs() =>
        JsonSerializer.Serialize(hls.GetActiveJobs(), Json);

    [McpServerTool(Name = "preprocess")]
    [Description(
        "Dispara a preparação do HLS de um título, para ele ficar pronto antes de alguém tentar assistir. " +
        "Devolve na hora; acompanhe com list_transcode_jobs. Use o caminho que search_media devolveu.")]
    public async Task<string> Preprocess(
        [Description("Caminho relativo à biblioteca, ex.: 'Filmes/Duna.mkv'.")] string path)
    {
        try
        {
            // SafeUnder é o que impede um caminho com ../ sair da biblioteca. O argumento vem de
            // um agente, então é entrada não confiável como qualquer outra — a mesma proteção
            // que a rota /api/preprocess já usa.
            var full = Helpers.PathHelper.SafeUnder(settings.LibraryRoot, path);

            if (!File.Exists(full))
            {
                return Problem($"não achei '{path}' na biblioteca. Use search_media para ver os caminhos válidos.");
            }

            var started = await hls.EnsurePreprocessAsync(full);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                path,
                started,
                message = started
                    ? "preparação iniciada; acompanhe com list_transcode_jobs"
                    : "já estava pronto ou já em andamento",
            }, Json);
        }
        catch (UnauthorizedAccessException)
        {
            return Problem($"'{path}' sai da biblioteca");
        }
        catch (Exception ex)
        {
            return Problem(ex.Message);
        }
    }

    /// <summary>Erro como texto acionável: quem chama é um agente e precisa conseguir corrigir.</summary>
    private static string Problem(string message) =>
        JsonSerializer.Serialize(new { ok = false, problem = message }, Json);
}
