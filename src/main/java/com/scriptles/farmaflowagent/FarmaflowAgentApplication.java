package com.scriptles.farmaflowagent;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;

@SpringBootApplication
public class FarmaflowAgentApplication {

    public static void main(String[] args) {
        SpringApplication application = new SpringApplication(FarmaflowAgentApplication.class);
        application.setHeadless(false);
        application.run(args);
    }

}
