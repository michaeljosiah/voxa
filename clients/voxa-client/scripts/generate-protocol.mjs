// Generates src/protocol.ts from ../voxa-wire.schema.json — itself generated from the C# wire
// envelope records by Voxa.Transports.WebSocket.Tests.WireSchemaGoldenTests. One source of truth:
// C# records -> schema (golden-checked in .NET CI) -> protocol.ts (golden-checked here via --check).
//
// Purpose-built instead of json-schema-to-typescript so the output is exactly the discriminated
// unions the client needs (literal "type" tags), deterministic, and dependency-free.
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const schemaPath = join(here, "..", "voxa-wire.schema.json");
const outPath = join(here, "..", "src", "protocol.ts");

const schema = JSON.parse(readFileSync(schemaPath, "utf8"));
const defs = schema.$defs;

/** JSON-schema property -> TypeScript type + optionality. */
function propType(node) {
  if (node.const !== undefined) return { ts: JSON.stringify(node.const), optional: false };
  if (node.enum) return { ts: node.enum.map((v) => JSON.stringify(v)).join(" | "), optional: false };
  const types = Array.isArray(node.type) ? node.type : [node.type];
  const nullable = types.includes("null");
  const core = types.filter((t) => t !== "null").map((t) =>
    t === "integer" || t === "number" ? "number" : t === "string" ? "string" : t === "boolean" ? "boolean" : "unknown");
  // The server omits null fields (WhenWritingNull) and the parser tolerates absence, so
  // nullable-in-schema means optional-on-the-wire.
  return { ts: core.join(" | ") + (nullable ? " | null" : ""), optional: nullable };
}

function emitDef(name, def) {
  const lines = [`export type ${name}Message = {`];
  for (const [prop, node] of Object.entries(def.properties)) {
    const { ts, optional } = propType(node);
    lines.push(`  ${prop}${optional ? "?" : ""}: ${ts};`);
  }
  lines.push("};");
  return lines.join("\n");
}

function emitUnion(name, def) {
  const members = def.oneOf.map((r) => r.$ref.split("/").pop() + "Message");
  return `export type ${name} =\n  | ${members.join("\n  | ")};`;
}

const envelopeNames = Object.keys(defs).filter((k) => k !== "ServerMessage" && k !== "ClientMessage");

const out = [
  "// AUTO-GENERATED from voxa-wire.schema.json (which is generated from the C# wire envelope",
  "// records). Do not edit by hand. Regenerate: npm run generate",
  "",
  `export const VOXA_WIRE_VERSION = ${schema.wireVersion};`,
  "",
  envelopeNames.map((n) => emitDef(n, defs[n])).join("\n\n"),
  "",
  emitUnion("ServerMessage", defs.ServerMessage),
  "",
  emitUnion("ClientMessage", defs.ClientMessage),
  "",
].join("\n");

if (process.argv.includes("--check")) {
  const committed = readFileSync(outPath, "utf8").replaceAll("\r\n", "\n");
  if (committed !== out) {
    console.error("protocol.ts is stale — the schema changed without regeneration. Run: npm run generate");
    process.exit(1);
  }
  console.log("protocol.ts is up to date.");
} else {
  writeFileSync(outPath, out);
  console.log(`wrote ${outPath}`);
}
