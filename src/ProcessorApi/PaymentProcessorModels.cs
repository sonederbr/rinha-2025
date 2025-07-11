namespace ProcessorApi;

record PaymentRequest(string CorrelationId, decimal Amount);
record PaymentResponse(string CorrelationId, bool Success);
record ServiceHealth(bool Failing, int MinResponseTime);
record PaymentSummary(int TotalRequests = 0, decimal TotalAmount = 0.0m);