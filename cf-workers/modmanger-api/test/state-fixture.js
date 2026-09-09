// Test-only clock/state controls. This file is never Wrangler's entrypoint.
// All decisions under test run unchanged in the production AuthState methods.
import worker, { AuthState } from "../src/index.js";
export default worker;
export class StateFixture extends AuthState {
  async fetch(request) {
    const path = new URL(request.url).pathname;
    if (path === "/test/expire") {
      this.sql.exec("UPDATE otp SET expires_at = ?", Date.now() - 1);
      return new Response("ok");
    }
    if (path === "/test/allow-resend") {
      this.sql.exec("UPDATE otp SET send_after = ?", Date.now() - 1);
      return new Response("ok");
    }
    if (path === "/test/previous-window") {
      this.sql.exec("UPDATE rate_limit SET bucket = bucket - 1, end_at = ?", Date.now() - 1);
      return new Response("ok");
    }
    return super.fetch(request);
  }
}
