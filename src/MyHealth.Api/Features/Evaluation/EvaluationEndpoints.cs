using MyHealth.Api.Domain;

namespace MyHealth.Api.Features.Evaluation;

public record EvaluateItem(MetricType Metric, double? Value, double? Secondary);

public record EvaluatedItem(
    MetricType Metric,
    double? Value,
    double? Secondary,
    string DisplayValue,
    HealthStatus Status,
    string StatusLabel);

public static class EvaluationEndpoints
{
    public static IEndpointRouteBuilder MapEvaluationEndpoints(this IEndpointRouteBuilder app)
    {
        // Анонимно: чистая функция оценки норм, без хранения данных.
        // Любой клиент может прислать значения и получить статус/форматирование.
        app.MapPost("/api/metrics/evaluate", (List<EvaluateItem> items) =>
        {
            var result = items.Select(i =>
            {
                var status = MetricEvaluator.Evaluate(i.Metric, i.Value, i.Secondary);
                var display = i.Value is double v
                    ? MetricEvaluator.Format(i.Metric, v, i.Secondary)
                    : "—";
                return new EvaluatedItem(
                    i.Metric, i.Value, i.Secondary, display,
                    status, MetricEvaluator.Label(status));
            }).ToList();
            return Results.Ok(result);
        }).WithTags("Evaluation");

        return app;
    }
}
