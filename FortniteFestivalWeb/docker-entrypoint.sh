#!/bin/sh
set -e

# Default backend URL if not provided
: "${API_BACKEND_URL:=http://fstservice:8080}"
export API_BACKEND_URL

# Substitute environment variables in the nginx config template
envsubst '${API_BACKEND_URL}' < /etc/nginx/templates/default.conf.template > /etc/nginx/conf.d/default.conf

exec nginx -g 'daemon off;'
