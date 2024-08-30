import Heading from "@theme/Heading";
import Link from "@docusaurus/Link";
import styles from "./styles.module.css";

export default function HomepageFeatures() {
  return (
    <section className={styles.features}>
      <div className="container">
        <div className="row">
          <div className="col col--offset-1 col--5">
            <Heading as="h2">Documentation</Heading>
            <ul>
              <li>
                <Link to="docs/intro">Introducing API Publisher</Link>
              </li>
              <li>
                <Link to="docs/configuration">
                  Configuration Notes
                </Link>
              </li>
              <li>
                <Link to="docs/cloud">Cloud Documentation</Link>
              </li>
              <li>
                <Link to="docs/configurationstore">Configuration Store Configuration</Link>
              </li>
            </ul>
            <Heading as="h2"></Heading>
          </div>
          <div className="col col--5">
            <Heading as="h2"> Project Guidelines & Notices</Heading>
            <ul>
              <li>
                <Link to="https://github.com/Ed-Fi-Alliance-OSS/Project-Tanager/blob/main/CONTRIBUTING.md">How to Contribute</Link>
              </li>
              <li>
                <Link to="https://github.com/Ed-Fi-Alliance-OSS/Project-Tanager/blob/main/CONTRIBUTING.md">Contributor Code of Conduct</Link>
              </li>
              <li>
                <Link to="https://github.com/Ed-Fi-Alliance-OSS/Project-Tanager/blob/main/CONTRIBUTING.md">List of Contributors</Link>
              </li>
              <li>
                <Link to="https://github.com/Ed-Fi-Alliance-OSS/Project-Tanager/blob/main/NOTICES.md">Copyright and License Notices</Link>
              </li>
              <li>
                <Link to="https://github.com/Ed-Fi-Alliance-OSS/Project-Tanager/blob/main/LICENSE">License</Link>
              </li>
            </ul>
          </div>
        </div>
      </div>
    </section>
  );
}
