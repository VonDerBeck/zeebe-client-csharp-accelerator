using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zeebe.Client.Api.Responses;
using Zeebe.Client.Accelerator.Extensions;
using Zeebe.Client.Accelerator.Integration.Tests.Handlers;
using Zeebe.Client.Accelerator.Integration.Tests.Helpers;
using Zeebe.Client.Accelerator.Abstractions;
using System.Linq;

namespace Zeebe.Client.Accelerator.Integration.Tests
{
    public class Test : IAsyncLifetime
    {
        private List<IJob> jobs;

        private readonly IntegrationTestHelper helper;

        public Test()
        {
            this.helper = new IntegrationTestHelper((job, cancellationToken) => this.jobs.Add(job));
        }

        [Fact]
        public async Task JobHandlerIsExecutedWhenProcesHasStarted()
        {   
            jobs = new List<IJob>();

            var zeebeClient = this.helper.ZeebeClient;            

            var deployResponse = await zeebeClient.NewDeployCommand()
                .AddResourceFile(GetResourceFile("simple-test.bpmn"))
                .Send();

            Assert.True(deployResponse.Key > 0);

            var processInstance = await zeebeClient.NewCreateProcessInstanceCommand()
                .BpmnProcessId("SimpleTest")
                .LatestVersion()
                .Send();

            Assert.NotNull(processInstance);
            
            WaitForHandlersToComplete(1, 1500);

            Assert.True(this.jobs.Count == 1);
        } 
        
        [Fact]
        public async Task BusinessExceptionIsCorrectlyPropegatedWhenProcesHasStarted()
        {            
            jobs = new List<IJob>();
            
            var zeebeClient = this.helper.ZeebeClient;            

            var deployResponse = await zeebeClient.NewDeployCommand()
                .AddResourceFile(GetResourceFile("exception-test.bpmn"))
                .Send();

            Assert.True(deployResponse.Key > 0);

            var processInstance = await zeebeClient.NewCreateProcessInstanceCommand()
                .BpmnProcessId("ExceptionTest")
                .LatestVersion()
                .Send();

            Assert.NotNull(processInstance);
            
            WaitForHandlersToComplete(2, 1500);

            Assert.True(this.jobs.Count == 2);
        }

        [Fact]
        public async Task InAndOutputVariablesAreCorrectlySerializedWhenProcesHasStarted()
        {
            var expectedGuid = Guid.NewGuid();

            jobs = new List<IJob>();

            var zeebeClient = this.helper.ZeebeClient;

            var deployResponse = await zeebeClient.NewDeployCommand()
                .AddResourceFile(GetResourceFile("variables-test.bpmn"))
                .Send();

            Assert.True(deployResponse.Key > 0);

            var processInstance = await zeebeClient.NewCreateProcessInstanceCommand()
                .BpmnProcessId("VariablesTest")
                .LatestVersion()
                .State(new {
                    Guid = expectedGuid
                })
                .Send();

            Assert.NotNull(processInstance);

            WaitForHandlersToComplete(2, 1500);

            Assert.True(this.jobs.Count == 2);

            var expected = OutputJobHandler.State;
            var actual = jobs[1] as ZeebeJob<InputState>;

            Assert.NotNull(actual);
            Assert.NotNull(actual.getVariables());
            var state = actual.getVariables();

            Assert.Equal(expected.Bool, state.Bool);
            Assert.Equal(expected.Int, state.Int);
            Assert.Equal(expected.Guid, expectedGuid);
            Assert.Equal(expected.DateTime, state.DateTime);
            Assert.Equal(expected.Int, state.Int);
            Assert.Equal(expected.String, state.String);
            Assert.Equal(expected.Double, state.Double);
            Assert.Null(state.ToBeIgnored);
            Assert.Equal(expected.MyJsonPropertyName, state.JsonPropertyNamedAttr);

            var doneMessage = zeebeClient.ReceiveMessage<DoneMessage>("responseFor_" + expectedGuid, TimeSpan.FromSeconds(5));
            Assert.Equal(expected.Guid, doneMessage.Guid);
            Assert.Equal(expected.DateTime, doneMessage.DateTime);
        }

        [Fact]
        public async Task UsesMultipleThreadsWhenConfigured()
        {
            jobs = new List<IJob>();
            
            var zeebeClient = this.helper.ZeebeClient;
            var deployResponse = await zeebeClient.NewDeployCommand()
                .AddResourceFile(GetResourceFile("thread-test.bpmn"))
                .Send();
            Assert.True(deployResponse.Key > 0);

            var input = new List<int>();
            for (int i = 0; i < 50; i++) input.Add(i);

            var correlationId = Guid.NewGuid();
            await zeebeClient.NewCreateProcessInstanceCommand()
                .BpmnProcessId("MultiThreadTest")
                .LatestVersion()
                .State(new
                {
                    InputCollection = input,
                    CorrelationId = correlationId
                })
                .Send();
            WaitForHandlersToComplete(50, 5000);

            var variables = zeebeClient.ReceiveMessage<MultiThreadVariables>("responseFor_" + correlationId, TimeSpan.FromSeconds(25));
            Assert.True(variables.UsedThreads.Distinct().Count() >= 3); // not exact due to the underlying TPL implementation internals

        }

        public async Task InitializeAsync()
        {
            await this.helper.InitializeAsync();
        }

        async Task IAsyncLifetime.DisposeAsync()
        {
            await this.helper.DisposeAsync();
        }

        private string GetResourceFile(string bpmn)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", bpmn);
        }

        private void WaitForHandlersToComplete(int jobCountWhenReady, int timeoutInMs)
        {
            var timeout = DateTime.Now.AddMilliseconds(timeoutInMs);
            while(DateTime.Now < timeout && jobs.Count < jobCountWhenReady)
            {
                Thread.Sleep(100);
            }

            Thread.Sleep(500);
        }

        private class DoneMessage
        {
            public Guid Guid { get; set; }
            public DateTime DateTime { get; set; }
        }

        private class MultiThreadVariables
        {
            public List<int> UsedThreads { get; set; }
        }
    }
}