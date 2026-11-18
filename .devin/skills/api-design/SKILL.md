---
name: api-design
description: Opinions for the HTTP contract of an ASP.NET Core controller API, covering preconditions, status codes, OpenAPI declarations, and request and response types. Not for write retry or concurrency design, or other frameworks.
---
Rules for the HTTP contract of ASP.NET Core controllers:
- Answer a missing `If-Match` on PUT and DELETE with 428: declare `[FromHeader(Name = "If-Match"), Required] string? ifMatch` so the OpenAPI document lists it as required, but `[ApiController]` turns the missing header into an automatic 400, so remove the `If-Match` entry from `ModelState` in an `IActionFilter` with `Order = int.MinValue` and throw the 428 from the service after the body checks; an invalid body or id mismatch stays 400, and keep `[ApiController]` on every controller so its operations stay in the document.
- Annotate every action with `[ProducesResponseType]` for each status it can return, including statuses raised by filters and services (201, 200, 204, 304 and `ProblemDetails` with content type `application/problem+json` for 400, 404, 409, 410, 412 and 428), so the generated OpenAPI document declares exactly what the API answers.
- Set the quoted `ETag` response header on every create, read and update response, computed with `SHA256` and never `MD5` or a counter, and make GET honor `If-None-Match`: a matching value answers 304 with no body.
- Take a `CancellationToken` parameter in every controller action and pass it through the services to every EF Core call (`SaveChangesAsync(cancellationToken)`, `SingleOrDefaultAsync(cancellationToken)`), never `CancellationToken.None`.
- Return response types such as records from controllers, never EF entities, and keep counters, analytics, timestamps and other server-owned or derived values out of every request and response schema.
- Declare the request bodies of POST and PUT as required with a non-nullable body parameter, never `[FromBody] T?` or `EmptyBodyBehavior.Allow`, so the OpenAPI document marks the body as required.
