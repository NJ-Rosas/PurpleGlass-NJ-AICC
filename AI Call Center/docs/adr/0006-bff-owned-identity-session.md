# ADR 0006: BFF-owned identity and browser session

## Status

Accepted for the synthetic security prototype.

## Decision

PurpleGlass.WebBff owns OpenID Connect authorization-code/PKCE callbacks and a bounded ASP.NET Core authentication cookie. React receives neither access nor refresh tokens and stores no session credential in Redux or browser storage.

An external provider subject is resolved by the provider-neutral Identity application boundary to one server-owned user and active membership. The membership determines tenant, allowed locations, role, and permissions. Route, query, body, header, or Redux values never grant membership. The BFF creates `RequestContext` only after cookie authentication and membership resolution, and application/persistence queries retain tenant and location predicates.

Development authentication is available only in the Development environment, requires `Security:AllowDevelopmentAuthentication=true`, and maps the selectors `administrator` and `read-only` to predefined synthetic identities. Production startup rejects that scheme.

## Consequences

- State-changing BFF requests require an ASP.NET Core antiforgery request token.
- HTTP and SSE endpoints share authentication and permission policies.
- The session cookie is HttpOnly, Secure in production, SameSite=Lax, non-sliding, and bounded.
- Production requires OIDC settings, restricted hosts, an HTTPS origin, persistent data-protection keys, and server-owned identity mappings.
- The selected OIDC vendor and durable identity-administration workflow remain open decisions.
- This boundary does not make the prototype production-ready or HIPAA-ready.
