using System;
using UnityEngine;
using UnityEngine.AI;
namespace LastShift
{
    /// <summary>Rate-limited reachable goals and local detours for a tiger's existing NavMeshAgent.</summary>
    public sealed class TigerNavigation
    {
        readonly NavMeshAgent agent; readonly Func<Vector3,bool> allowed;
        readonly NavMeshPath candidate = new NavMeshPath();
        Vector3 goal, checkpoint; float nextPlan, nextRecovery, progressAt, nextAttach;
        bool hasGoal;
        public int Recoveries {get;private set;}
        public TigerNavigation(NavMeshAgent agent,Func<Vector3,bool> allowed)
        {
            this.agent=agent;this.allowed=allowed;checkpoint=agent.transform.position;progressAt=Time.time;
            agent.autoRepath=true;agent.acceleration=20f;agent.angularSpeed=600f;
            agent.obstacleAvoidanceType=ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            agent.avoidancePriority=25+Mathf.Abs(agent.GetEntityId().GetHashCode()%40);
        }
        NavMeshQueryFilter Filter => new NavMeshQueryFilter {agentTypeID=agent.agentTypeID,areaMask=agent.areaMask};
        public bool Attach()
        {
            if(!agent.enabled)return false;
            if(agent.isOnNavMesh)return true;
            if(Time.time<nextAttach)return false;nextAttach=Time.time+0.75f;
            if(NavMesh.SamplePosition(agent.transform.position,out var hit,2.5f,Filter) && Mathf.Abs(hit.position.y-agent.transform.position.y)<1.5f)
                return agent.Warp(hit.position);
            return false;
        }
        public bool Move(Vector3 target,float speed)
        {
            if(!Attach())return false;
            agent.isStopped=false;agent.speed=speed;
            bool changed=!hasGoal || (target-goal).sqrMagnitude>1f;
            if(Time.time>=nextPlan || changed)
            {
                nextPlan=Time.time+0.4f;goal=target;hasGoal=true;
                Plan(target);
            }
            float distance=Vector3.Distance(agent.transform.position,target);
            if(Time.time-progressAt>=1.2f)
            {
                bool stuck=(agent.transform.position-checkpoint).sqrMagnitude<0.04f && distance>1.8f && !agent.pathPending;
                checkpoint=agent.transform.position;progressAt=Time.time;
                if(stuck && Time.time>=nextRecovery)
                {nextRecovery=Time.time+1.8f;Detour(target);}
            }
            return agent.hasPath;
        }
        bool Plan(Vector3 target)
        {
            if(!allowed(target))return false;
            Vector3 best=default;float bestScore=float.PositiveInfinity;bool found=false;
            for(int i=0;i<13;i++)
            {
                Vector3 offset=i==0?Vector3.zero:Quaternion.Euler(0,(i-1)*60f,0)*Vector3.forward*(i<=6?1.5f:3.5f);
                if(!NavMesh.SamplePosition(target+offset,out var hit,1.8f,Filter) || !allowed(hit.position))continue;
                if(!agent.CalculatePath(hit.position,candidate) || candidate.status==NavMeshPathStatus.PathInvalid || candidate.corners.Length<2)continue;
                Vector3 end=candidate.corners[candidate.corners.Length-1];
                float score=(end-target).sqrMagnitude;
                if(candidate.status==NavMeshPathStatus.PathComplete && i==0){agent.SetPath(candidate);return true;}
                if(score<bestScore && (end-agent.transform.position).sqrMagnitude>0.16f){bestScore=score;best=hit.position;found=true;}
            }
            if(found && agent.CalculatePath(best,candidate)){agent.SetPath(candidate);return true;}
            // Keep a useful path while the target momentarily steps off the walkable surface.
            return false;
        }
        void Detour(Vector3 target)
        {
            Vector3 direction=target-agent.transform.position;direction.y=0;
            if(direction.sqrMagnitude<0.1f)return;direction.Normalize();
            Vector3 side=Vector3.Cross(Vector3.up,direction);float sign=(Recoveries%2==0)?1:-1;
            for(int i=0;i<6;i++)
            {
                Vector3 point=agent.transform.position+side*sign*(i<2?1.8f:3f)+direction*(i<4?1.2f:-1.5f);
                sign=-sign;
                if(!NavMesh.SamplePosition(point,out var hit,1f,Filter) || !allowed(hit.position))continue;
                if(agent.CalculatePath(hit.position,candidate) && candidate.status==NavMeshPathStatus.PathComplete && candidate.corners.Length>1)
                {agent.ResetPath();agent.SetPath(candidate);nextPlan=Time.time+1f;Recoveries++;return;}
            }
            nextPlan=0;agent.autoRepath=true;
        }
        public void ResetProgress(){checkpoint=agent.transform.position;progressAt=Time.time;hasGoal=false;nextPlan=0;}
    }
}
