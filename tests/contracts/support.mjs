import { readFile } from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';
import SwaggerParser from '@apidevtools/swagger-parser';

export const root = path.resolve(import.meta.dirname, '../..');
export async function json(file) { return JSON.parse(await readFile(path.join(root, file), 'utf8')); }
export const catalog = await json('contracts/events/checking/catalog.json');
const ajv = new Ajv2020({ strict: true, strictRequired: false, allErrors: true, coerceTypes: false });
addFormats(ajv);
export const schemas = new Map();
const validators = new Map();
for (const entry of catalog) {
  const schema = await json(entry.schema);
  schemas.set(entry.name, schema);
  validators.set(entry.name, ajv.compile(schema));
}

// Standard JSON Schema cannot compare two arbitrary instance fields. These checks
// describe the required adapter semantics, without an Ajv-specific wire dialect.
export function validateMessage(name, message, { producer = false } = {}) {
  const validator = validators.get(name);
  assert.ok(validator, `Unknown message ${name}`);
  assert.ok(validator(message), `${name}: ${ajv.errorsText(validator.errors)}`);
  const payload = message.payload;
  const aggregateId = name === 'RecheckRequested' ? payload.appealId
    : name === 'RecheckRequestRejected' ? payload.recheckRequestId : payload.checkId;
  assert.equal(message.aggregateId, aggregateId, 'Envelope aggregate identity mismatch');
  if (name === 'RecheckAssigned' && payload.assignmentKind === 'Selected')
    assert.equal(payload.reviewerId, payload.selectedReviewerId, 'Selected reviewer mismatch');
  if (name === 'CheckCompleted' && payload.decisionOrigin === 'AppealReview')
    assert.equal(payload.reviewerId, payload.selectedReviewerId, 'AppealReview reviewer mismatch');
  if (name === 'RecheckEscalated')
    assert.equal(payload.releasedReviewerId, payload.selectedReviewerId, 'Released reviewer mismatch');
  if (producer) {
    // Producer output is restricted to declared fields; readers tolerate optional
    // additive extensions. Sensitive/legacy fields are forbidden by schema.
    const schema = schemas.get(name);
    for (const key of Object.keys(message)) assert.ok(key in schema.properties, `Undeclared envelope field ${key}`);
    for (const key of Object.keys(payload)) assert.ok(key in schema.properties.payload.properties, `Undeclared payload field ${key}`);
  }
  return message;
}

export const openapiPath = path.join(root, 'contracts/http/checking/openapi.json');
export const openapi = await SwaggerParser.validate(openapiPath, { resolve: { http: false } });
const httpAjv = new Ajv2020({ strict: true, strictRequired: false, allErrors: true, coerceTypes: false });
addFormats(httpAjv);
function rewriteHttpRefs(value) {
  if (Array.isArray(value)) return value.map(rewriteHttpRefs);
  if (!value || typeof value !== 'object') return value;
  return Object.fromEntries(Object.entries(value).map(([key, item]) => [key,
    key === '$ref' ? item.replace('#/components/schemas/', '#/$defs/') : rewriteHttpRefs(item)]));
}
const httpRoot = { $id: 'urn:matigr:checking:http:v1', $defs: rewriteHttpRefs(openapi.components.schemas) };
httpAjv.addSchema(httpRoot);
const httpValidators = new Map(Object.keys(httpRoot.$defs).map(name =>
  [name, httpAjv.compile({ $ref: `${httpRoot.$id}#/$defs/${name}` })]));
export function validateHttp(name, value) {
  const validator = httpValidators.get(name);
  assert.ok(validator, `Unknown HTTP schema ${name}`);
  assert.ok(validator(value), `${name}: ${httpAjv.errorsText(validator.errors)}`);
  return value;
}
export function resolveObject(value) {
  if (!value?.$ref) return value;
  assert.ok(value.$ref.startsWith('#/'), 'Only local OpenAPI refs are permitted');
  return resolveObject(value.$ref.slice(2).split('/').reduce((object, key) => object[key], openapi));
}
