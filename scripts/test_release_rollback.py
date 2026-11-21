import os
import shutil
import subprocess
import unittest
from pathlib import Path


def find_bash():
    if os.name != "nt":
        return shutil.which("bash")
    git = shutil.which("git")
    if git:
        for parent in Path(git).parents:
            candidate = parent / "bin" / "bash.exe"
            if candidate.is_file():
                return str(candidate)
    candidate = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe"
    return str(candidate) if candidate.is_file() else None


BASH = find_bash()


@unittest.skipUnless(BASH, "Bash is required for the server rollback checks.")
class ReleaseRollbackTests(unittest.TestCase):
    def exercise_rollback(self, *, previous_dashboard, schema_touched, dashboard_restores=True, switch_attempted=True):
        source = Path(__file__).with_name("deploy-release.sh").read_text(encoding="utf-8")
        handler = source[source.index("on_exit() {"):source.index("trap on_exit EXIT")]
        bash_flag = lambda value: "true" if value else "false"
        script = f"""
set -euo pipefail
exec 3>&1
previous_compose=previous-compose.yml
rollback_tag=rollback-previous
previous_image=sha256:previous
backup=/root/royaltyflow-backup.test/royaltyflow.dump
previous_dashboard={bash_flag(previous_dashboard)}
schema_touched={bash_flag(schema_touched)}
switch_attempted={bash_flag(switch_attempted)}
compose() {{ printf 'restore %s\n' "$*" >&3; }}
wait_health() {{ printf 'health %s\n' "$1"; }}
verify_dashboard() {{ printf 'dashboard check\n'; {bash_flag(dashboard_restores)}; }}
{handler}
trap on_exit EXIT
exit 17
"""
        result = subprocess.run([BASH, "--noprofile", "--norc"], input=script.encode(),
                                capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 17)
        return result.stdout.decode(), result.stderr.decode()

    def test_dashboard_failure_restores_pinned_previous_image_and_checks_its_dashboard(self):
        output, log = self.exercise_rollback(previous_dashboard=True, schema_touched=False)
        self.assertIn("restore previous-compose.yml rollback-previous up -d --no-deps --no-build --pull never royaltyflow-api", output)
        self.assertIn("health sha256:previous", output)
        self.assertIn("dashboard check", output)
        self.assertIn("Previous release restored", log)

    def test_first_release_can_restore_a_legacy_api_only_image(self):
        output, log = self.exercise_rollback(previous_dashboard=False, schema_touched=False)
        self.assertIn("health sha256:previous", output)
        self.assertNotIn("dashboard check", output)
        self.assertIn("Previous release restored", log)

    def test_incompatible_schema_leaves_the_database_and_backup_untouched(self):
        output, log = self.exercise_rollback(previous_dashboard=True, schema_touched=True)
        self.assertEqual(output, "")
        self.assertIn("/root/royaltyflow-backup.test/royaltyflow.dump", log)

    def test_unsuccessful_restore_reports_retained_image(self):
        _, log = self.exercise_rollback(previous_dashboard=True, schema_touched=False, dashboard_restores=False)
        self.assertIn("Automatic application rollback failed", log)
        self.assertIn("royaltyflow-api:rollback-previous", log)

    def test_build_or_preflight_failure_does_not_restart_previous_image(self):
        output, log = self.exercise_rollback(previous_dashboard=True, schema_touched=False, switch_attempted=False)
        self.assertEqual((output, log), ("", ""))


if __name__ == "__main__":
    unittest.main()
