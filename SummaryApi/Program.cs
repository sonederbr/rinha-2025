using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var paymentsDefault = new ConcurrentBag<Payment>();
var paymentsFallback = new ConcurrentBag<Payment>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapPost("/default-summary", (Payment payment) =>
{
    paymentsDefault.Add(payment);
    Console.WriteLine("Payment default added");
    return Results.Accepted("Payment default accepted for storing.");
}).WithName("PostDefaultSummary");

app.MapPost("/fallback-summary", (Payment payment) =>
{
    paymentsFallback.Add(payment);
    Console.WriteLine("Payment fallback added");
    return Results.Accepted("Payment fallback accepted for storing.");
}).WithName("PostFallbackSummary");

app.MapGet("/default-summary", ([FromQuery] string? fromReq, [FromQuery] string? toReq) =>
{
    DateTimeOffset? from = null;
    DateTimeOffset? to = null;

    if (DateTimeOffset.TryParse(fromReq, out var parsedFrom))
        from = parsedFrom;

    if (DateTimeOffset.TryParse(toReq, out var parsedTo))
        to = parsedTo;
    
    var filteredPayments = paymentsDefault
        .Where(p => (!from.HasValue || p.RequestedAt >= from) && (!to.HasValue || p.RequestedAt <= to))
        .ToList();
    
    return Results.Ok(filteredPayments);
}).WithName("GetDefaultSummary");

app.MapGet("/fallback-summary", ([FromQuery] string? fromReq, [FromQuery] string? toReq) =>
{
    DateTimeOffset? from = null;
    DateTimeOffset? to = null;

    if (DateTimeOffset.TryParse(fromReq, out var parsedFrom))
        from = parsedFrom;

    if (DateTimeOffset.TryParse(toReq, out var parsedTo))
        to = parsedTo;
    
    var filteredPayments = paymentsDefault
        .Where(p => (!from.HasValue || p.RequestedAt >= from) && (!to.HasValue || p.RequestedAt <= to))
        .ToList();
    
    return Results.Ok(filteredPayments);
}).WithName("GetFallbackSummary");

app.Run();

public record Payment(decimal Amount, DateTimeOffset RequestedAt);