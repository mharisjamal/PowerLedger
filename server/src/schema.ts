import { Validator, type OutputUnit, type Schema } from "@cfworker/json-schema";
import reportSchema from "../schema/report-v1.schema.json";

// Built once at module scope; a second Validator for the same $id would be refused.
const validator = new Validator(reportSchema as Schema, "2020-12", false);

/** The first problem the schema finds with `report`, or null when it's valid. */
export function firstSchemaError(report: unknown): OutputUnit | null {
  const result = validator.validate(report);
  return result.valid ? null : (result.errors[0] ?? null);
}
