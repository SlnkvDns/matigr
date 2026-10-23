import { readFile } from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';
import SwaggerParser from '@apidevtools/swagger-parser';

export const root = path.resolve(import.meta.dirname, '../..');
export async function json(file) { return JSON.parse(await readFile(path.join(root, file), 'utf8')); }
export const checkingCatalog = await json('contracts/events/checking/catalog.json');
export const resultsCatalog = await json('contracts/events/results/catalog.json');
export const appealsCatalog = await json('contracts/events/appeals/catalog.json');
export const catalog = [...checkingCatalog, ...resultsCatalog, ...appealsCatalog];
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
  const aggregateId = name.endsWith('RecheckRequested') ? payload.appealId
    : name.endsWith('RecheckRequestRejected') ? payload.recheckRequestId
      : name.endsWith('ResultEvaluationApplied') || name.endsWith('ResultEvaluationRejected') ? payload.evaluationId : payload.checkId;
  assert.equal(message.aggregateId, aggregateId, 'Envelope aggregate identity mismatch');
  if (name.endsWith('RecheckAssigned') && payload.assignmentKind === 'Selected')
    assert.equal(payload.reviewerId, payload.selectedReviewerId, 'Selected reviewer mismatch');
  if (name.endsWith('CheckCompleted') && payload.decisionOrigin === 'AppealReview')
    assert.equal(payload.reviewerId, payload.selectedReviewerId, 'AppealReview reviewer mismatch');
  if (name.endsWith('RecheckEscalated'))
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

export const checkingOpenapiPath = path.join(root, 'contracts/http/checking/openapi.json');
export const resultsOpenapiPath = path.join(root, 'contracts/http/results/openapi.json');
export const appealsOpenapiPath = path.join(root, 'contracts/http/appeals/openapi.json');
export const openapi = await SwaggerParser.validate(checkingOpenapiPath, { resolve: { http: false } });
export const resultsOpenapi = await SwaggerParser.validate(resultsOpenapiPath, { resolve: { http: false } });
export const appealsOpenapi = await SwaggerParser.validate(appealsOpenapiPath, { resolve: { http: false } });
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
const resultsHttpRoot = { $id: 'urn:matigr:results:http:v1', $defs: rewriteHttpRefs(resultsOpenapi.components.schemas) };
httpAjv.addSchema(resultsHttpRoot);
const resultsHttpValidators = new Map(Object.keys(resultsHttpRoot.$defs).map(name =>
  [name, httpAjv.compile({ $ref: `${resultsHttpRoot.$id}#/$defs/${name}` })]));
export function validateResultsHttp(name, value) {
  const validator = resultsHttpValidators.get(name);
  assert.ok(validator, `Unknown Results HTTP schema ${name}`);
  assert.ok(validator(value), `${name}: ${httpAjv.errorsText(validator.errors)}`);
  return value;
}
const appealsHttpRoot = { $id: 'urn:matigr:appeals:http:v1', $defs: rewriteHttpRefs(appealsOpenapi.components.schemas) };
httpAjv.addSchema(appealsHttpRoot);
const appealsHttpValidators = new Map(Object.keys(appealsHttpRoot.$defs).map(name =>
  [name, httpAjv.compile({ $ref: `${appealsHttpRoot.$id}#/$defs/${name}` })]));
export const appealsBasisSchema = await json('contracts/http/appeals/results-appeal-basis.v1.schema.json');
const appealsBasisValidator = httpAjv.compile(appealsBasisSchema);
export function validateAppealsHttp(name, value) {
  const validator = appealsHttpValidators.get(name);
  assert.ok(validator, `Unknown Appeals HTTP schema ${name}`);
  assert.ok(validator(value), `${name}: ${httpAjv.errorsText(validator.errors)}`);
  return value;
}
export function validateAppealBasis(value) {
  assert.ok(appealsBasisValidator(value), `ResultsAppealBasis: ${httpAjv.errorsText(appealsBasisValidator.errors)}`);
  return value;
}
export function resolveObject(value) {
  if (!value?.$ref) return value;
  assert.ok(value.$ref.startsWith('#/'), 'Only local OpenAPI refs are permitted');
  return resolveObject(value.$ref.slice(2).split('/').reduce((object, key) => object[key], openapi));
}
export function resolveResultsObject(value) {
  if (!value?.$ref) return value;
  assert.ok(value.$ref.startsWith('#/'), 'Only local Results OpenAPI refs are permitted');
  return resolveResultsObject(value.$ref.slice(2).split('/').reduce((object, key) => object[key], resultsOpenapi));
}
export function resolveAppealsObject(value) {
  if (!value?.$ref) return value;
  assert.ok(value.$ref.startsWith('#/'), 'Only local Appeals OpenAPI refs are permitted');
  return resolveAppealsObject(value.$ref.slice(2).split('/').reduce((object, key) => object[key], appealsOpenapi));
}
