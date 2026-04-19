#!/usr/bin/env bash
# cloud-teardown.sh — Stop Docker Compose stack and print VM destroy commands.
# Run this on the test host after Phase 3 is complete.

set -eu

echo "=== Saving final compose logs ==="
docker compose logs --no-color > results/cloud/compose-final.log 2>&1 || true
echo "Logs saved to results/cloud/compose-final.log"

echo "=== Stopping containers and removing volumes ==="
docker compose down -v

echo ""
echo "=== Destroy VMs (run on your control machine) ==="
echo "AWS:"
echo "  aws ec2 terminate-instances --instance-ids <test-host-id> <k6-runner-id>"
echo "  aws ec2 describe-instances --instance-ids <test-host-id> <k6-runner-id> \\"
echo "    --query 'Reservations[].Instances[].State.Name'"
echo ""
echo "GCP:"
echo "  gcloud compute instances delete fleans-test-host fleans-k6-runner --zone <zone> --quiet"
echo ""
echo "Azure:"
echo "  az vm delete -g fleans-load-rg -n fleans-test-host fleans-k6-runner --yes"
echo "  az group delete -n fleans-load-rg --yes  # to remove all associated resources"
echo ""
echo "Teardown complete. Verify VMs are terminated to avoid unexpected charges."
