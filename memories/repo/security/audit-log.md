# Security Audit Log

## Full System Audit (2026-04-03)

**Scope**: FSTService + FortniteFestivalWeb + Docker deployment  
**Agent**: Security Agent  
**Output**: `/memories/repo/architecture/security-posture.md`

### Findings Summary

| Severity | Count | Key Issues |
|---|---|---|
| 🔴 HIGH | 1 | Dev secrets in appsettings.json (committed to git) |
| ⚠️ MEDIUM | 3 | SQL interpolation (3 instances), missing nginx security headers, identical rate limit tiers |
| ℹ️ LOW | 3 | No CSRF tokens (acceptable), unencrypted credential file, permissive input validation |

### Remediation Status

- [ ] Parameterize `maxScore`, `top`, `offset` in InstrumentDatabase.cs (lines 409, 423-424, 503)
- [ ] Add security headers to nginx.conf (X-Frame-Options, X-Content-Type-Options, HSTS, CSP)
- [ ] Differentiate rate limit tiers (lower limits for auth/protected policies)
- [ ] Move dev secrets from appsettings.json to User Secrets
- [ ] Add input length/format validation on API endpoints

> Lesson: The project has strong fundamentals (parameterized queries at ~98%, non-root Docker, fail-closed auth, no secrets in logs). Main gaps are defense-in-depth hardening rather than exploitable vulnerabilities.
