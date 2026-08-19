using System.Net;
using System.Text;

namespace Contoso.Lending.WorkflowTests;

/// <summary>
/// Stands in for the layer-2 API: returns canned JSON bodies per path so the tests can assert the
/// workflow passes service output through untouched. Records every request for assertions.
/// </summary>
public sealed class StubServiceApi : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> responses = [];

    public List<(string Path, string Body)> Requests { get; } = [];

    public StubServiceApi Enqueue(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        if (!responses.TryGetValue(path, out var queue))
        {
            queue = new Queue<(HttpStatusCode, string)>();
            responses[path] = queue;
        }

        queue.Enqueue((status, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        lock (Requests)
        {
            Requests.Add((path, body));
        }

        if (!responses.TryGetValue(path, out var queue) || queue.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"no stub for {path}", Encoding.UTF8, "application/json"),
            };
        }

        var (status, responseBody) = queue.Count == 1 ? queue.Peek() : queue.Dequeue();
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };
    }
}
