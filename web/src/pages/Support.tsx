// SPDX-License-Identifier: LGPL-3.0-or-later
import Layout from '../layouts/Layout'
import { PageDoc, Chapter, Cta } from '../components/doc'

export default function Support() {
  return (
    <Layout
      title="Support — OKF4net"
      description="Two ways to support OKF4net: a free GitHub Sponsors contribution, or paid dedicated support (SLA, integration help, custom development, training) via Coderise."
      current="support"
    >
      <PageDoc
        path={
          <>
            my_bundle/<b>support.md</b>
          </>
        }
        type="Guide"
        title={
          <>
            Two ways to <em>get involved.</em>
          </>
        }
        lede={
          <>
            OKF4net is free and open source under <strong>LGPL-3.0-or-later</strong> — sponsorship is never
            required to use it. If you'd like to support the project anyway, there are two options below.
          </>
        }
      />

      <div className="docbody">
        <Chapter id="sponsor" title="Sponsor the project" refText="optional, not required">
          <p>
            OKF4net's GitHub Sponsors listing funds ongoing maintenance and spec-fidelity work — golden fixture
            upkeep, tracking the OKF spec, CI across Linux/Windows/macOS. It's a bonus, not a paywall: the
            library, CLI, and every source project stay LGPL-3.0-or-later regardless.
          </p>
          <div className="hero-actions">
            <a className="btn primary" href="https://github.com/sponsors/jchable">
              Sponsor on GitHub ↗
            </a>
          </div>
        </Chapter>

        <Chapter id="dedicated-support" title="Need dedicated help?" refText="paid, via Coderise">
          <p>
            For teams that need a guaranteed response time, hands-on integration help, custom development, or
            training on OKF4net, Coderise — the studio behind OKF4net — offers paid dedicated support.
          </p>
          <Cta title="Priority support, integration help, custom dev, training.">
            <p>Annual SLA plans, per-incident support, and quote-based engagements for everything else.</p>
            <div className="hero-actions">
              <a className="btn primary" href="https://okf4net.oss.coderise.fr">
                okf4net.oss.coderise.fr ↗
              </a>
            </div>
          </Cta>
        </Chapter>
      </div>
    </Layout>
  )
}
