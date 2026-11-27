using System.Text.Json;
using VMCrate.Core;

namespace VMCrate.Host;

internal static class QueueEndpoints
{
    internal static void Map(WebApplication app, QueueSettings settings, JobLedger jobs)
    {
        var routes = app.MapGroup(RemoteProtocol.Prefix);
        routes.AddEndpointFilter(async (call, next) =>
        {
            var context = call.HttpContext;
            var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
            var account = settings.ForCertificate(certificate);
            if (account is null || context.Request.Headers[RemoteProtocol.Header] != RemoteProtocol.Version)
                return Failure("error.remoteCertificate", StatusCodes.Status403Forbidden);
            context.Items[typeof(QueueAccount)] = account;
            context.Response.Headers.CacheControl = "no-store";
            try { return await next(call); }
            catch (LabException error) { return Failure(error.Key, StatusCodes.Status409Conflict); }
            catch (JsonException) { return Failure("error.remoteJob", StatusCodes.Status400BadRequest); }
        });

        routes.MapGet("/recipes", (HttpContext context) => Replies(Account(context).Recipes.Values.Select(recipe => recipe.Summary).ToArray()));
        routes.MapGet("/jobs", (HttpContext context) => Replies(jobs.List(Account(context))));
        routes.MapPost("/jobs", async (HttpContext context) =>
        {
            if (!context.Request.HasJsonContentType())
                return Failure("error.remoteJob", StatusCodes.Status415UnsupportedMediaType);
            var request = await context.Request.ReadFromJsonAsync<RemoteRequest>(RemoteProtocol.Json, context.RequestAborted);
            return request is null ? Failure("error.remoteJob", StatusCodes.Status400BadRequest)
                : Replies(jobs.Submit(Account(context), request), StatusCodes.Status202Accepted);
        });
        routes.MapPost("/jobs/{id:guid}/retry", (HttpContext context, Guid id) =>
            Replies(jobs.Retry(Account(context), id), StatusCodes.Status202Accepted));
    }

    private static QueueAccount Account(HttpContext context) => (QueueAccount)context.Items[typeof(QueueAccount)]!;

    private static IResult Replies<T>(T reply, int status = 200) => Results.Json(reply, RemoteProtocol.Json, statusCode: status);
    private static IResult Failure(string code, int status) => Replies(new RemoteFailure(code), status);
}
