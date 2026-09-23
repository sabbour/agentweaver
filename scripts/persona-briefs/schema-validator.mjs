import fs from 'node:fs';

import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv);
const validators = new Map();

function validatorFor(schemaPath) {
  if (!validators.has(schemaPath)) {
    const schema = JSON.parse(fs.readFileSync(schemaPath, 'utf8'));
    validators.set(schemaPath, ajv.compile(schema));
  }
  return validators.get(schemaPath);
}

export function validateJsonSchema(schemaPath, value, label = 'value') {
  const validate = validatorFor(schemaPath);
  if (validate(value)) return { ok: true, errors: [] };
  return {
    ok: false,
    errors: validate.errors.map((error) => {
      const location = error.instancePath ? `${label}${error.instancePath}` : label;
      return `${location} ${error.message}`;
    }),
  };
}
