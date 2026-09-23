import { Validator, type OutputUnit, type Schema } from "@cfworker/json-schema";
import reportSchema from "../schema/report-v1.schema.json";

// Built once at module scope; a second Validator for the same $id would be refused.
const validator = new Validator(reportSchema as Schema, "2020-12", false);

/** The first problem the schema finds with `report`, or null when it's valid. */
export function firstSchemaError(report: unknown): OutputUnit | null {
  const result = validator.validate(report);
  return result.valid ? null : (result.errors[0] ?? null);
}

export const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

// /v1/consent's body: the installId pattern plus the schema's own `consent` definition, reused
// rather than copied.
const consentRequestSchema = {
  type: "object",
  additionalProperties: false,
  required: ["installId", "consent"],
  properties: {
    installId: { type: "string", pattern: GUID_PATTERN.source },
    consent: (reportSchema as Schema).$defs!.consent,
  },
} satisfies Schema;
const consentValidator = new Validator(consentRequestSchema, "2020-12", false);

/** The first problem the schema finds with a /v1/consent body, or null when it's valid. */
export function firstConsentError(body: unknown): OutputUnit | null {
  const result = consentValidator.validate(body);
  return result.valid ? null : (result.errors[0] ?? null);
}
