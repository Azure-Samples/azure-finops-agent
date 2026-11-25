import assert from "node:assert/strict";
import test from "node:test";

import {
  isValidTenantIdentifier,
  normalizeTenantIdentifier,
  withTenantIdentifier,
} from "../../src/frontend/src/lib/authTenant.js";

test("tenant identifiers accept GUIDs and tenant domains", () => {
  assert.equal(
    normalizeTenantIdentifier("Contoso.OnMicrosoft.com"),
    "contoso.onmicrosoft.com",
  );
  assert.equal(
    isValidTenantIdentifier("11111111-2222-3333-4444-555555555555"),
    true,
  );
});

test("tenant identifiers reject URLs, IPs, malformed labels and paths", () => {
  for (const value of [
    "https://contoso.onmicrosoft.com",
    "contoso.onmicrosoft.com/path",
    "contoso.onmicrosoft.com:443",
    "127.0.0.1",
    "contoso.123",
    "contoso..onmicrosoft.com",
    "-contoso.onmicrosoft.com",
  ]) {
    assert.equal(isValidTenantIdentifier(value), false, value);
  }
});

test("tenant overrides are added only to the requested OAuth URL", () => {
  assert.equal(
    withTenantIdentifier(
      "/auth/microsoft?tier=base",
      "Contoso.OnMicrosoft.com",
    ),
    "/auth/microsoft?tier=base&tenant=contoso.onmicrosoft.com",
  );
  assert.equal(
    withTenantIdentifier("/auth/microsoft?tier=licenses", ""),
    "/auth/microsoft?tier=licenses",
  );
});
