## Summary
- What changed?
- Why is this needed?

## Validation
- [ ] `docker build -t dirforge:test .`
- [ ] `docker run --rm -p 8091:8080 -v /srv/share:/data:ro dirforge:test`
- [ ] Manual smoke test completed (include steps)

## Checklist
- [ ] Kept changes focused and backward-safe
- [ ] Updated docs/config examples if behavior changed
- [ ] Linked related issue(s), if any
