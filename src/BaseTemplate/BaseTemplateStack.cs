using System.Collections.Generic;
using System.Text;
using Amazon.CDK;
using Amazon.CDK.AWS.DirectoryService;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.SecretsManager;
using Amazon.CDK.AWS.SSM;
using Constructs;
using static Amazon.CDK.AWS.DirectoryService.CfnMicrosoftAD;
using static Amazon.CDK.AWS.EC2.CfnInstance;

namespace BaseTemplate
{
    public class BaseTemplateStack : Stack
    {
        internal BaseTemplateStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            /*
             * This parameter automatically retrieves the current WIndows AMI ID.
             */
            Amazon.CDK.CfnParameter WindowsImage = new Amazon.CDK.CfnParameter(this, "windows-image", new Amazon.CDK.CfnParameterProps
            {
                Type = "AWS::SSM::Parameter::Value<AWS::EC2::Image::Id>",
                Default = "/aws/service/ami-windows-latest/Windows_Server-2019-English-Full-Base"
            });
    
            /*
             * Create a random password in Secrets manager.
             */
            var adminSecret = new Secret(this, "mmad-admin-user-secret", new SecretProps
            {
                Description = "Common Administrator password",
                GenerateSecretString = new SecretStringGenerator()
                {
                    ExcludeCharacters = "\"@/\\",
                    PasswordLength = 30,
                    SecretStringTemplate = "{\"Username\":\"Admin\"}",
                    GenerateStringKey = "Password"
                },
                SecretName = "MMADAdminSecret"
            });

            /*
             * Create a VPC to launch your EC2 instances and Managed Active DIrectory into.
             */
            var vpc = new Vpc(this, "lab-vpc", new VpcProps()
            {
                Cidr = @"10.0.0.0/16",
                EnableDnsHostnames = true,
                EnableDnsSupport = true,
                MaxAzs = 2,
                SubnetConfiguration = new SubnetConfiguration[]
                {
                    new SubnetConfiguration ()
                    {
                        CidrMask = 24,
                        Name = "Public",
                        SubnetType = SubnetType.PUBLIC
                    },
                    new SubnetConfiguration ()
                    {
                        CidrMask = 24,
                        Name = "Private",
                        SubnetType = SubnetType.PRIVATE_WITH_NAT
                    }
                }
            });

            /*
             * Create an IAM role, and provide permissions to to acess Systems Manager, and join 
             * to Active Directory
             */
            var role = new Role(this, "lab-role", new RoleProps()
            {
                AssumedBy = new ServicePrincipal("ec2.amazonaws.com"),
                RoleName = "lab-role"
            });
            role.AddManagedPolicy(ManagedPolicy.FromAwsManagedPolicyName("AmazonSSMManagedInstanceCore"));
            role.AddManagedPolicy(ManagedPolicy.FromAwsManagedPolicyName("CloudWatchAgentServerPolicy"));
            role.AddManagedPolicy(ManagedPolicy.FromAwsManagedPolicyName("AWSDirectoryServiceFullAccess"));

            var instanceProfile = new CfnInstanceProfile(this, "lab-role-profile", new CfnInstanceProfileProps()
            {
                InstanceProfileName = "lab-role",
                Roles = new string[] { "lab-role" }
            });

            /*
             * Create a Microsoft Managed Active Directory, and use the random password for the Admin account.
             */
            var cfnMicrosoftAD = new CfnMicrosoftAD(this, "MMAD", new CfnMicrosoftADProps
            {
                Name = "corp.local",
                ShortName = "CORP",
                Password = "{{resolve:secretsmanager:MMADAdminSecret:SecretString:Password}}",
                VpcSettings = new VpcSettingsProperty
                {
                    SubnetIds = new[] { vpc.PrivateSubnets[0].SubnetId, vpc.PrivateSubnets[1].SubnetId },
                    VpcId = vpc.VpcId
                },

                // the properties below are optional
                Edition = "Standard"
            });
            cfnMicrosoftAD.Node.AddDependency(adminSecret);

            /*
             * Code to create a Systems Manager automation document
             * that will be used to automatically join the instance to
             * the managed active directory.
             */
            Dictionary<string, object> directoryIdProp = new Dictionary<string, object>();
            directoryIdProp.Add("Ref", "MMAD");

            Dictionary<string, object> dnsIpAddressesProp = new Dictionary<string, object>();
            dnsIpAddressesProp.Add("Fn::GetAtt", new string[] { "MMAD", "DnsIpAddresses"});

            Dictionary<string, object> properties = new Dictionary<string, object>();
            properties.Add("directoryId", directoryIdProp);
            properties.Add("directoryName", "CORP.local");
            properties.Add("dnsIpAddresses", dnsIpAddressesProp );

            Dictionary<string, object> domainjoin = new Dictionary<string, object>();
            domainjoin.Add("properties", properties);

            Dictionary<string, object> runtimeConfig = new Dictionary<string, object>();
            runtimeConfig.Add("aws:domainJoin", domainjoin);

            Dictionary<string, object> docProperties = new Dictionary<string, object>();
            docProperties.Add("schemaVersion", "1.2");
            docProperties.Add("description", "Join the instance to a MMAD domain");
            docProperties.Add("runtimeConfig", runtimeConfig);

            var ssmDocument2 = new CfnDocument(this, "mmad-ssd-doc", new CfnDocumentProps()
            {
                Name = "MMAD-AD-Association",
                Content = docProperties
            }); ;


            /*
             * Create a security group for our EC2 instances.
             * Allow all trafic from the VPC, but no trafic external to the VPC.
             */
            SecurityGroup publicSecurityGroup = new SecurityGroup(this, "public-security-group", new SecurityGroupProps
            {
                Vpc = vpc,
                Description = "Public Security Group for the Jump Box",
                SecurityGroupName = "PublicSecurityGroup"

            });
            publicSecurityGroup.AddIngressRule(Peer.Ipv4(@"10.0.0.0/16"), Port.AllTraffic(), "Allow all trafic from the VPC");

            SecurityGroup privateSecurityGroup = new SecurityGroup(this, "private-security-group", new SecurityGroupProps
            {
                Vpc = vpc,
                Description = "Private Security Group for the SQL Servers",
                SecurityGroupName = "PrivateSecurityGroup"

            });
            privateSecurityGroup.AddIngressRule(Peer.Ipv4(@"10.0.0.0/16"), Port.AllTraffic(), "Allow all trafic from the VPC");

            /*
             * Create an EC2 instance that automatically joins itself to the Managed Active Directory.
             */
            CfnInstance jumpBox = new CfnInstance(this, "jump-box-instance", new CfnInstanceProps
            {
                InstanceType = "t3.large",
                ImageId = WindowsImage.ValueAsString,
                IamInstanceProfile = "lab-role",
                SecurityGroupIds = new[] { privateSecurityGroup.SecurityGroupId },
                SsmAssociations = new[] { new SsmAssociationProperty
                    {
                        DocumentName = ssmDocument2.Name
                    }
                },
                SubnetId = vpc.PublicSubnets[0].SubnetId,
                Tags = new[] {
                    new CfnTag {
                        Key = "Name",
                        Value = "EC2 Jump"
                    }
                }
            }); ;
            jumpBox.AddDependsOn(ssmDocument2);

            /*
             * Store parameters from this stack in Parameter store.
             * These can be used later by other stacks that need to retrieve values
             * from this stack.
             */
            new StringParameter(this, "vpc-id", new StringParameterProps()
            {
                Description = "ID For the created VPC",
                ParameterName = "vpc-id",
                StringValue = vpc.VpcId
            });

            new StringParameter(this, "vpc-public-subnet-1", new StringParameterProps()
            {
                Description = "Public Subnet 1 ID",
                ParameterName = "pub-subnet-1",
                StringValue = vpc.PublicSubnets[0].SubnetId
            });

            new StringParameter(this, "vpc-public-subnet-2", new StringParameterProps()
            {
                Description = "Public Subnet 2 ID",
                ParameterName = "pub-subnet-2",
                StringValue = vpc.PublicSubnets[1].SubnetId
            });

            new StringParameter(this, "vpc-private-subnet-1", new StringParameterProps()
            {
                Description = "Private Subnet 1 ID",
                ParameterName = "private-subnet-1",
                StringValue = vpc.PrivateSubnets[0].SubnetId
            });

            new StringParameter(this, "vpc-private-subnet-2", new StringParameterProps()
            {
                Description = "Private Subnet 2 ID",
                ParameterName = "private-subnet-2",
                StringValue = vpc.PrivateSubnets[1].SubnetId
            });

            new StringParameter(this, "domain-fqdn", new StringParameterProps()
            {
                Description = "Fully Qualified Domain Name",
                ParameterName = "FQDN",
                StringValue = "corp.local"
            });

            new StringParameter(this, "domain-short", new StringParameterProps()
            {
                Description = "Short Domain Name",
                ParameterName = "Domain",
                StringValue = "CORP"
            });

            new StringParameter(this, "domain-join-doc", new StringParameterProps()
            {
                Description = "Document to use to domain join to the pre-created domain",
                ParameterName = "MMADJoinDoc",
                StringValue = ssmDocument2.Name
            });

            new StringParameter(this, "instance-role", new StringParameterProps()
            {
                Description = "Preconfigured Instance Role",
                ParameterName = "InstanceRoleName",
                StringValue = role.RoleName
            });

            new StringParameter(this, "public-security-group-parm", new StringParameterProps()
            {
                Description = "Public Security Group for Jump Poxes",
                ParameterName = "PublicSG",
                StringValue = publicSecurityGroup.SecurityGroupId
            });

            new StringParameter(this, "private-security-group-parm", new StringParameterProps()
            {
                Description = "Private Security Group For Servers",
                ParameterName = "PrivateSG",
                StringValue = privateSecurityGroup.SecurityGroupId
            });

        }
    }
}
