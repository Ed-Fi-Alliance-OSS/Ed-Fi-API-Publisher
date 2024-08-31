"use strict";(self.webpackChunked_fi_api_publisher=self.webpackChunked_fi_api_publisher||[]).push([[1416],{3258:(e,n,i)=>{i.r(n),i.d(n,{assets:()=>d,contentTitle:()=>s,default:()=>h,frontMatter:()=>o,metadata:()=>a,toc:()=>c});var t=i(4848),r=i(8453);const o={},s="Running in Docker Desktop.",a={id:"tech/Considerations-docker-configuration-and-execution",title:"Running in Docker Desktop.",description:"Docker Containers have the added benefit of running anywhere (e.g. VMs, on-premises in the cloud), which is a massive advantage for both developers and deployment. Leading cloud providers, including Google Cloud, Amazon Web Services (AWS) and Microsoft Azure have adopted it. For simplicity the steps below describe how to use Docker Compose to deploy the Ed-Fi Api Publisher and related tools on Docker Desktop.",source:"@site/docs/tech/Considerations-docker-configuration-and-execution.md",sourceDirName:"tech",slug:"/tech/Considerations-docker-configuration-and-execution",permalink:"/Ed-Fi-API-Publisher/docs/tech/Considerations-docker-configuration-and-execution",draft:!1,unlisted:!1,editUrl:"https://github.com/ed-fi-alliance-oss/Ed-Fi-API-Publisher/tree/gh-pages/pages/docs/docs/tech/Considerations-docker-configuration-and-execution.md",tags:[],version:"current",frontMatter:{},sidebar:"tutorialSidebar",previous:{title:"Ed-Fi API Publisher Configuration",permalink:"/Ed-Fi-API-Publisher/docs/tech/API-Publisher-Configuration"},next:{title:"Considerations for API Hosts",permalink:"/Ed-Fi-API-Publisher/docs/tech/Considerations-for-API-Hosts"}},d={},c=[{value:"Step 1. Download the Source Code or Clone the Repo",id:"step-1-download-the-source-code-or-clone-the-repo",level:2},{value:"Step 2. Setup Your Environment Variables",id:"step-2-setup-your-environment-variables",level:2},{value:"Change Configuration on .env file",id:"change-configuration-on-env-file",level:2},{value:"2.a. Nuget Version",id:"2a-nuget-version",level:3},{value:"2.b. Section ApiPublisherSettings file",id:"2b-section-apipublishersettings-file",level:3},{value:"2.c. Section ConfigurationStoreSettings and Plain Text Connections",id:"2c-section-configurationstoresettings-and-plain-text-connections",level:3},{value:"Step 3. Run Docker Compose",id:"step-3-run-docker-compose",level:2},{value:"Step 4. Verify Your Deployments",id:"step-4-verify-your-deployments",level:2},{value:"Step 5. Run API Publisher",id:"step-5-run-api-publisher",level:2},{value:"Outside Container.",id:"outside-container",level:3},{value:"Inside Container.",id:"inside-container",level:3},{value:"AWS Parameter Store.",id:"aws-parameter-store",level:2},{value:"Configuration",id:"configuration",level:3},{value:"B. Execution",id:"b-execution",level:3},{value:"C. Validate Results.",id:"c-validate-results",level:3},{value:"Configuration",id:"configuration-1",level:2},{value:"Execution.",id:"execution",level:2}];function l(e){const n={a:"a",blockquote:"blockquote",code:"code",h1:"h1",h2:"h2",h3:"h3",img:"img",p:"p",pre:"pre",...(0,r.R)(),...e.components};return(0,t.jsxs)(t.Fragment,{children:[(0,t.jsx)(n.h1,{id:"running-in-docker-desktop",children:"Running in Docker Desktop."}),"\n",(0,t.jsx)(n.p,{children:"Docker Containers have the added benefit of running anywhere (e.g. VMs, on-premises in the cloud), which is a massive advantage for both developers and deployment. Leading cloud providers, including Google Cloud, Amazon Web Services (AWS) and Microsoft Azure have adopted it. For simplicity the steps below describe how to use Docker Compose to deploy the Ed-Fi Api Publisher and related tools on Docker Desktop."}),"\n",(0,t.jsx)(n.h1,{id:"setup",children:"Setup"}),"\n",(0,t.jsx)(n.h2,{id:"step-1-download-the-source-code-or-clone-the-repo",children:"Step 1. Download the Source Code or Clone the Repo"}),"\n",(0,t.jsx)(n.p,{children:"The Ed-Fi ODS Docker deployment source code is in the Ed-Fi repository hosted by GitHub. A link to the repository is provided in the download panel on the right. You can clone the repository or download the source code as a ZIP file."}),"\n",(0,t.jsx)(n.p,{children:(0,t.jsx)(n.a,{href:"https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher",children:"https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher"})}),"\n",(0,t.jsx)(n.h2,{id:"step-2-setup-your-environment-variables",children:"Step 2. Setup Your Environment Variables"}),"\n",(0,t.jsx)(n.p,{children:"Configure your deployments using an environment file. The repository includes a .env.example listing the supported environment variables."}),"\n",(0,t.jsxs)(n.blockquote,{children:["\n",(0,t.jsx)(n.p,{children:"Path:   /src/Compose/env.example"}),"\n"]}),"\n",(0,t.jsx)(n.p,{children:(0,t.jsx)(n.img,{src:i(8573).A+"",width:"676",height:"522"})}),"\n",(0,t.jsx)(n.p,{children:"Copy env.example file and name it .env. Update the values as desired."}),"\n",(0,t.jsx)(n.pre,{children:(0,t.jsx)(n.code,{className:"language-docker",children:"# Nuget version\r\nVERSION=<Package to install from https://dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_artifacts/feed/EdFi/NuGet/EdFi.ApiPublisher/ Eg. 0.0.0-alpha.0.38>\r\n\r\n# The section option for apiPublisherSettings file\r\nBEARER_TOKEN_REFRESH_MINUTES=<Default. 28>\r\nRETRY_STARTING_DELAY_MILLISECONDS=<Default. 100>\r\nMAX_RETRY_ATTEMPTS=<Default. 10>\r\nMAX_DEGREE_OF_PARALLELISM_FOR_RESOURCE_PROCESSING=<Default. 10>\r\nMAX_DEGREE_OF_PARALLELISM_FOR_POST_RESOURCE_ITEM=<Default. 20>\r\nMAX_DEGREE_OF_PARALLELISM_FOR_STREAM_RESOURCE_PAGES=<Default. 5>\r\nSTREAMING_PAGES_WAIT_DURATION_SECONDS=<Default. 10>\r\nSTREAMING_PAGE_SIZE=<Default. 75>\r\nINCLUDE_DESCRIPTORS=<Default. false>\r\nERROR_PUBLISHING_BATCH_SIZE=<Default. 25>\r\nUSE_CHANGE_VERSION_PAGING=<Default. false>\r\nCHANGE_VERSION_PAGING_WINDOW_SIZE=<Default. 25000>\r\n\r\n# The file configurationStoreSettings\r\nPROVIDER=<Could be one of the following values: sqlServer, postgreSql, awsParameterStore, or plainText. Default. plainText>\r\nSQLSERVER_SERVER=<If PROVIDER is sqlServer this is required Eg. (local)>\r\nSQLSERVER_DATABASE=<If PROVIDER is sqlServer this is required Eg. EdFi_API_Publisher_Configuration>\r\nPOSTGRESQL_HOST=<If PROVIDER is postgreSql this is required Eg. localhost>\r\nPOSTGRESQL_DATABASE=<If PROVIDER is postgreSql this is required Eg. edfi_api_publisher_configuration>\r\nAWS_PROFILE=<If PROVIDER is awsParameterStore this is required Eg. default>\r\nAWS_REGION=<If PROVIDER is awsParameterStore this is required Eg. us-east-1>\r\n\r\n# PlainText connections\r\nSOURCE_NAME=<Name for the source connection Eg. Hosted_Sample_v5.2>\r\nSOURCE_URL=<Url for the source connection Eg. https://api.ed-fi.org/v5.2/api/>\r\nSOURCE_KEY=<Key for the source connection Eg. RvcohKz9zHI4>RvcohKz9zHI4\r\nSOURCE_SECRET=<Secret for the source connection Eg. E1iEFusaNf81xzCxwHfbolkC>\r\n\r\nTARGET_NAME=<Name for the target connection Eg. Local_v5.2>\r\nTARGET_URL=<Url for the target connection Eg. http://localhost:54746/>\r\nTARGET_KEY=<Key for the target connection Eg. RvcohKz9zHI4>\r\nTARGET_SECRET=<Secret for the target connection Eg. E1iEFusaNf81xzCxwHfbolkC>\r\n\r\n# Logging using Serilog\r\nWRITE_TO_FILE_PATH=<Path to store the logging file Eg. ../tmp/logs/Ed-Fi-API-PublisherSerilog.log>\n"})}),"\n",(0,t.jsx)(n.p,{children:"Sample .env provide all the different parameters for run ApiPublisher with different configurations. Please provide the information necessary for a specific configuration."}),"\n",(0,t.jsx)(n.h2,{id:"change-configuration-on-env-file",children:"Change Configuration on .env file"}),"\n",(0,t.jsx)(n.h3,{id:"2a-nuget-version",children:"2.a. Nuget Version"}),"\n",(0,t.jsxs)(n.p,{children:['Note: If you want to run a different version than the release, you can modify the version in this file using the name of the generated build. (Docker file i.e. ENV VERSION="1.0.1-alpha.0.17")\r\n',(0,t.jsx)(n.img,{src:i(3406).A+"",width:"694",height:"318"})]}),"\n",(0,t.jsx)(n.p,{children:(0,t.jsx)(n.img,{src:i(2323).A+"",width:"1112",height:"540"})}),"\n",(0,t.jsx)(n.h3,{id:"2b-section-apipublishersettings-file",children:"2.b. Section ApiPublisherSettings file"}),"\n",(0,t.jsx)(n.p,{children:"If you like, you can change the default parameters or leave them as is."}),"\n",(0,t.jsx)(n.p,{children:(0,t.jsx)(n.a,{href:"/Ed-Fi-API-Publisher/docs/tech/API-Publisher-Configuration",children:"API Publisher Configuration"})}),"\n",(0,t.jsx)(n.h3,{id:"2c-section-configurationstoresettings-and-plain-text-connections",children:"2.c. Section ConfigurationStoreSettings and Plain Text Connections"}),"\n",(0,t.jsxs)(n.p,{children:["These two sections can be configured using ",(0,t.jsx)(n.a,{href:"/Ed-Fi-API-Publisher/docs/tech/API-Connection-Management",children:"API Connection Management"})]}),"\n",(0,t.jsx)(n.h2,{id:"step-3-run-docker-compose",children:"Step 3. Run Docker Compose"}),"\n",(0,t.jsx)(n.p,{children:"In this step you need to run a command in any shell terminal that support Docker commands (i.e. PowerShell in Windows)"}),"\n",(0,t.jsx)(n.p,{children:"Go to the root of the project and run this command:"}),"\n",(0,t.jsx)(n.pre,{children:(0,t.jsx)(n.code,{children:"docker-compose -f src/Compose/compose-build.yml --env-file src/Compose/.env up -d\n"})}),"\n",(0,t.jsx)(n.h2,{id:"step-4-verify-your-deployments",children:"Step 4. Verify Your Deployments"}),"\n",(0,t.jsx)(n.p,{children:(0,t.jsx)(n.img,{src:i(9521).A+"",width:"1250",height:"558"})}),"\n",(0,t.jsx)(n.p,{children:"You can also check these files to see if all the settings in the .env file are used in the API Publisher configuration."}),"\n",(0,t.jsx)(n.p,{children:(0,t.jsx)(n.img,{src:i(3507).A+"",width:"1248",height:"788"})}),"\n",(0,t.jsx)(n.h2,{id:"step-5-run-api-publisher",children:"Step 5. Run API Publisher"}),"\n",(0,t.jsx)(n.p,{children:"We have two ways to run API Publisher inside or outside the created container."}),"\n",(0,t.jsx)(n.h3,{id:"outside-container",children:"Outside Container."}),"\n",(0,t.jsx)(n.pre,{children:(0,t.jsx)(n.code,{children:"docker exec -it ed-fi-ods-apipublisher dotnet EdFiApiPublisher.dll --sourceUrl={{SourceUrl}}/WebApi/ --sourceKey={{SourceKey}} --sourceSecret={{SourceSecret}} --targetUrl=https://{{TargetUrl}}/WebApi/ --targetKey={{TargetKey}} --targetSecret={{TargetSecret}} {{Additional Parameters}}\n"})}),"\n",(0,t.jsx)(n.p,{children:(0,t.jsx)(n.img,{src:i(9893).A+"",width:"1306",height:"206"})}),"\n",(0,t.jsx)(n.h3,{id:"inside-container",children:"Inside Container."}),"\n",(0,t.jsx)(n.p,{children:"Using Docker Dashboard it is possible to enter the container and use its terminal. Once inside it is necessary to run the command without the docker parameter... i.e."}),"\n",(0,t.jsx)(n.p,{children:(0,t.jsx)(n.img,{src:i(4796).A+"",width:"1254",height:"286"})}),"\n",(0,t.jsx)(n.pre,{children:(0,t.jsx)(n.code,{children:"dotnet EdFiApiPublisher.dll --sourceUrl={{SourceUrl}}/WebApi/ --sourceKey={{SourceKey}} --sourceSecret={{SourceSecret}} --targetUrl=https://{{TargetUrl}}/WebApi/ --targetKey={{TargetKey}} --targetSecret={{TargetSecret}} {{Additional Parameters}}\n"})}),"\n",(0,t.jsx)(n.p,{children:(0,t.jsx)(n.img,{src:i(3336).A+"",width:"1246",height:"360"})}),"\n",(0,t.jsx)(n.h1,{id:"additional-configurations",children:"Additional Configurations."}),"\n",(0,t.jsx)(n.h2,{id:"aws-parameter-store",children:"AWS Parameter Store."}),"\n",(0,t.jsx)(n.h3,{id:"configuration",children:"Configuration"}),"\n",(0,t.jsxs)(n.p,{children:["It is necessary to have the store parameters created on AWS ",(0,t.jsx)(n.a,{href:"/Ed-Fi-API-Publisher/docs/configurationstore/Aws-Parameter-Store",children:"Configuration Aws Parameter Store"})]}),"\n",(0,t.jsx)(n.p,{children:"Export AWS credentials to consume AWS parameters store inside the container"}),"\n",(0,t.jsx)(n.p,{children:(0,t.jsx)(n.img,{src:i(6595).A+"",width:"1248",height:"150"})}),"\n",(0,t.jsx)(n.h3,{id:"b-execution",children:"B. Execution"}),"\n",(0,t.jsx)(n.p,{children:"Having all this configured it is possible to run ApiPublisher with the AWSParameterStore parameter"}),"\n",(0,t.jsx)(n.pre,{children:(0,t.jsx)(n.code,{children:"dotnet EdFiApiPublisher.dll --configurationStoreProvider=awsParameterStore --sourceName=Ed-Fi-ApiPub01 --targetName=Ed-Fi-ApiPub02 --ignoreIsolation=true --maxRetryAttempts=4 --retryStartingDelayMilliseconds=1000 --streamingPageSize=1000 --maxDegreeOfParallelismForResourceProcessing=1 --includeOnly=grades,students\n"})}),"\n",(0,t.jsx)(n.p,{children:(0,t.jsx)(n.img,{src:i(6857).A+"",width:"1248",height:"132"})}),"\n",(0,t.jsx)(n.h3,{id:"c-validate-results",children:"C. Validate Results."}),"\n",(0,t.jsx)(n.p,{children:"After completing the execution the 'lastChangeVersion Processed' parameter value should be updated"}),"\n",(0,t.jsx)(n.p,{children:(0,t.jsx)(n.img,{src:i(7499).A+"",width:"1248",height:"710"})}),"\n",(0,t.jsx)(n.h1,{id:"cloudwatch",children:"CloudWatch."}),"\n",(0,t.jsx)(n.h2,{id:"configuration-1",children:"Configuration"}),"\n",(0,t.jsxs)(n.p,{children:["Configure the AWS log storage parameters in the configuration file.\r\n",(0,t.jsx)(n.a,{href:"/Ed-Fi-API-Publisher/docs/cloud/CloudWatch-configuration",children:"Configuration CloudWatch"})]}),"\n",(0,t.jsx)(n.h2,{id:"execution",children:"Execution."}),"\n",(0,t.jsx)(n.p,{children:"Run any ApiPublisher command to start storing the execution results."}),"\n",(0,t.jsx)(n.pre,{children:(0,t.jsx)(n.code,{children:"dotnet EdFiApiPublisher.dll --configurationStoreProvider=awsParameterStore --sourceName=Ed-Fi-ApiPub01 --targetName=Ed-Fi-ApiPub02 --ignoreIsolation=true --maxRetryAttempts=4 --retryStartingDelayMilliseconds=1000 --streamingPageSize=1000 --maxDegreeOfParallelismForResourceProcessing=1 --includeOnly=grades,students\n"})}),"\n",(0,t.jsx)(n.p,{children:"To review the results we have to go to the 'logGroup' and then look for the last logStream created."}),"\n",(0,t.jsx)(n.p,{children:(0,t.jsx)(n.img,{src:i(6112).A+"",width:"1186",height:"940"})})]})}function h(e={}){const{wrapper:n}={...(0,r.R)(),...e.components};return n?(0,t.jsx)(n,{...e,children:(0,t.jsx)(l,{...e})}):l(e)}},2323:(e,n,i)=>{i.d(n,{A:()=>t});const t=i.p+"assets/images/build-version-file-d7748e1a74c25c130cde3539e7559cd1.png"},3406:(e,n,i)=>{i.d(n,{A:()=>t});const t=i.p+"assets/images/build-version-d64942425b68177c473525d311fa413a.png"},7499:(e,n,i)=>{i.d(n,{A:()=>t});const t=i.p+"assets/images/changeVersion-updated-e088679d193bd1633c4a8e30c81effe5.png"},6112:(e,n,i)=>{i.d(n,{A:()=>t});const t=i.p+"assets/images/cloudwatch-datastream-87d6fadb7ef920cac4751fd2b03063e4.png"},9893:(e,n,i)=>{i.d(n,{A:()=>t});const t=i.p+"assets/images/console-command-a4e228202a6b5eff60119aaacffeafc0.png"},3507:(e,n,i)=>{i.d(n,{A:()=>t});const t=i.p+"assets/images/docker-files-modified-338631be575020cb3d4cd30d312349c6.png"},8573:(e,n,i)=>{i.d(n,{A:()=>t});const t=i.p+"assets/images/docker-path-1f9d29e7ee09340ea7cf1d9b4a405531.png"},3336:(e,n,i)=>{i.d(n,{A:()=>t});const t=i.p+"assets/images/docker-terminal-command-8bffc1e23d111c5ed9ee52b5c145eafe.png"},4796:(e,n,i)=>{i.d(n,{A:()=>t});const t=i.p+"assets/images/docker-terminal-8a79b9605a4951edd126ab8b42238b6f.png"},9521:(e,n,i)=>{i.d(n,{A:()=>t});const t=i.p+"assets/images/docker-validation-23efb187bdf1f1502625b85da894fd4a.png"},6857:(e,n,i)=>{i.d(n,{A:()=>t});const t=i.p+"assets/images/run-docker-command-7114786baf63df152247888cd482c295.png"},6595:(e,n,i)=>{i.d(n,{A:()=>t});const t=i.p+"assets/images/setup-aws-credentials-6e47b5672b844387f039fbe36316bed4.png"},8453:(e,n,i)=>{i.d(n,{R:()=>s,x:()=>a});var t=i(6540);const r={},o=t.createContext(r);function s(e){const n=t.useContext(o);return t.useMemo((function(){return"function"==typeof e?e(n):{...n,...e}}),[n,e])}function a(e){let n;return n=e.disableParentContext?"function"==typeof e.components?e.components(r):e.components||r:s(e.components),t.createElement(o.Provider,{value:n},e.children)}}}]);