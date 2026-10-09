#!/usr/bin/env python3
"""Validate Postman collections against the official v2.1 schema.

Usage: python3 build/pm-validate.py <schema.json> <collection.json> [...]

Fetch the schema once with:
  curl -sL -o /tmp/pm-schema.json \
    https://schema.getpostman.com/json/collection/v2.1.0/collection.json
Requires: pip install jsonschema
"""
import json
import sys

from jsonschema import validators


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: pm-validate.py <schema.json> <collection.json> [...]")
        return 2
    with open(sys.argv[1], "r", encoding="utf-8") as f:
        schema = json.load(f)
    validator_cls = validators.validator_for(schema)
    validator_cls.check_schema(schema)
    rc = 0
    for path in sys.argv[2:]:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        validator = validator_cls(schema)
        errors = sorted(validator.iter_errors(data), key=lambda e: list(e.path))
        print(path)
        if not errors:
            print("  SCHEMA OK")
            continue
        rc = 1
        for e in errors[:30]:
            loc = "/".join(str(p) for p in e.path) or "<root>"
            print(f"  ERROR at {loc}: {e.message}")
    return rc


if __name__ == "__main__":
    sys.exit(main())
