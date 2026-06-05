package com.scriptles.farmaflowagent.controller;

import com.scriptles.farmaflowagent.dto.AgentStatusResponse;
import com.scriptles.farmaflowagent.dto.UpdateCheckResponse;
import com.scriptles.farmaflowagent.service.AgentStatusService;
import com.scriptles.farmaflowagent.service.UpdateService;
import org.springframework.web.bind.annotation.CrossOrigin;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
@RequestMapping("/agent")
@CrossOrigin(
        origins = {
                "http://localhost:3000",
                "https://farmaflow-rho.vercel.app/"
        }
)
public class AgentController {

    private final AgentStatusService agentStatusService;
    private final UpdateService updateService;

    public AgentController(
            AgentStatusService agentStatusService,
            UpdateService updateService
    ) {
        this.agentStatusService = agentStatusService;
        this.updateService = updateService;
    }

    @GetMapping("/status")
    public AgentStatusResponse status() {
        return agentStatusService.status();
    }

    @GetMapping("/update/check")
    public UpdateCheckResponse checkUpdate() {
        return updateService.checkForUpdate();
    }
}
