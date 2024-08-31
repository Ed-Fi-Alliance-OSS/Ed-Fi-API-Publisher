import Heading from "@theme/Heading";

export default function About() {
  return (
    <section>
      <div className="container">
        <div className="row">
          <div className="col col--5 col--offset-1">
            <Heading as="h1">About</Heading>
            <p>
            The Ed-Fi API Publisher is a utility that can be used to move data and changes from one 
            Ed-Fi ODS API instance to another instance of the _same_ version of Ed-Fi. It operates 
            as a standard API client against both API endpoints (source and target) and thus it does 
            not require any special network configuration, direct ODS database access or a particular 
            database engine. From a data security/privacy perspective, it is also subject to all 
            authorization performed by the Ed-Fi ODS API endpoints with which it communicates.

            Operationally, it can be used in a "Pull" model where it is deployed alongside a target
             (central) API and gathers data from multiple source APIs.
            </p>


          </div>
          <div className="col col--5">
            <img src="img/push-central.png" />
          </div>
        </div>
      </div>
    </section>
  );
}
