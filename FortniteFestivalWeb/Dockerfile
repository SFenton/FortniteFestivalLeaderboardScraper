# ── Stage 1: Build React web app ──────────────────────────────
FROM node:20-slim AS webapp
WORKDIR /src
COPY packages/core/ packages/core/
COPY packages/theme/ packages/theme/
COPY packages/ui-utils/ packages/ui-utils/
COPY FortniteFestivalWeb/package.json FortniteFestivalWeb/yarn.lock FortniteFestivalWeb/
WORKDIR /src/FortniteFestivalWeb
# Use node-modules linker in Docker (local dev uses PnP with global cache)
RUN corepack enable && echo 'nodeLinker: node-modules' > .yarnrc.yml && yarn install
COPY FortniteFestivalWeb/ .
# Overwrite .yarnrc.yml again after COPY brings in project files
RUN echo 'nodeLinker: node-modules' > .yarnrc.yml && yarn vite build --outDir /webapp-dist

# ── Stage 2: Serve with Nginx ─────────────────────────────────
FROM nginx:stable-alpine AS runtime

# Copy built SPA into nginx html directory
COPY --from=webapp /webapp-dist/ /usr/share/nginx/html/

# Copy nginx config as a template (envsubst will process it at startup)
COPY FortniteFestivalWeb/nginx.conf /etc/nginx/templates/default.conf.template

# Copy entrypoint script
COPY FortniteFestivalWeb/docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

EXPOSE 80

ENTRYPOINT ["/docker-entrypoint.sh"]
